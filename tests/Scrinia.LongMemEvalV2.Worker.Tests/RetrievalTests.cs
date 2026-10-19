using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
using Scrinia.LongMemEvalV2.Worker.Retrieval;
namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class RetrievalTests
{
    private static readonly string[] ReadTools=["list","recall","search"];
    [Fact]
    public async Task Fresh_read_only_conversations_forward_question_image_and_materialize_real_sources()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();
        var learn=await fixture.RequestAsync();
        var receipt=await new TrajectoryLearner(_=>new Client((_,_)=>Text("Done."))).LearnAsync(learn,CancellationToken.None);
        var request=Request(learn,receipt);
        int calls=0;
        var retriever=new EvidenceRetriever(_=>new Client((messages,options)=> {
            calls++;
            Assert.Single(messages,m=>m.Role==ChatRole.User);
            Assert.Contains("Synthetic question?",messages.Last(m=>m.Role==ChatRole.User).Text);
            Assert.Single(messages.SelectMany(m=>m.Contents).OfType<DataContent>());
            Assert.Equal(ReadTools,options!.Tools!.Select(t=>t.Name).Order(StringComparer.Ordinal));
            return Text(Selection(learn));
        }));
        for(int i=0;i<2;i++) {
            var current=request with {OperationId=Guid.NewGuid().ToString("N"),Paths=request.Paths with {EventLogPath=Path.Combine(fixture.Root,$"retrieve{i}.jsonl")}};
            var result=await retriever.RetrieveEvidenceAsync(current,CancellationToken.None);
            var selected=result.Selection;
            Assert.Single(selected.References);
            Assert.Contains("source",File.ReadAllText(current.Paths.EventLogPath));
            Assert.Contains("disconnected",File.ReadAllText(current.Paths.EventLogPath));
            Assert.Contains(result.Items,e=>e.Type=="text" && e.Value.Contains("2026-03-04"));
            Assert.Equal(learn.Trajectory.States[0].Screenshot.Path,Assert.Single(result.Items,e=>e.Type=="image").Value);
        }
        Assert.Equal(2,calls);
        Assert.Equal(receipt.NewSnapshotHash,SnapshotValidator.Fingerprint(learn.StagingPath));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"references\":[],\"answer\":\"invented\"}")]
    [InlineData("{\"references\":[],\"references\":[]}")]
    [InlineData("{\"references\":[{\"kind\":\"source\",\"sourceId\":\"fabricated\",\"stateIndex\":0}]}")]
    public void Invalid_or_fabricated_selection_is_rejected(string json)
    {
        Assert.ThrowsAny<Exception>(()=>EvidenceSelection.Parse(json).Validate([]));
    }
    [Fact]
    public void Only_explicit_empty_selection_is_empty()
    {
        var selection=EvidenceSelection.Parse("{\"references\":[]}");
        selection.Validate([]);
        Assert.Empty(selection.References);
    }
    [Theory]
    [InlineData(false,40,5,2)]
    [InlineData(true,40,5,2)]
    [InlineData(false,1,5,1)]
    [InlineData(true,40,1,1)]
    public async Task Retrieve_recovers_loop_or_truncation_with_shared_limits(bool loop,int requests,int attempts,int expectedCalls)
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        var receipt=await new TrajectoryLearner(_=>new Client((_,_)=>Text("Done."))).LearnAsync(learn,CancellationToken.None);
        var request=Request(learn,receipt) with {Configuration=learn.Configuration with {MaxModelRequests=requests,MaxAttempts=attempts}};
        int calls=0;
        var retriever=new EvidenceRetriever(_=>new Client((_,_)=> {
            calls++;
            if(calls>1)return Text("{\"references\":[]}");
            IList<AIContent> contents=loop
                ? [new TextReasoningContent(string.Join("\n\n",Enumerable.Repeat("I have identified the relevant facts and should now store them using the available memory tool. Before doing so I will reconsider the same selection once more, although no new information has appeared.",30))),new TextContent("")]
                : [new FunctionCallContent("partial","recall",null)];
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,contents)) {ModelId="synthetic",FinishReason=ChatFinishReason.Length,Usage=new(){OutputTokenCount=16384}};
        }));
        if(expectedCalls==2)Assert.Empty((await retriever.RetrieveAsync(request,CancellationToken.None)).References);
        else await Assert.ThrowsAsync<InvalidDataException>(()=>retriever.RetrieveAsync(request,CancellationToken.None));
        Assert.Equal(expectedCalls,calls);
        Assert.Contains(loop?"reasoning-loop":"truncated-tool-call",File.ReadAllText(request.Paths.EventLogPath));
    }
    [Fact]
    public async Task Drift_and_mutation_calls_fail_before_dispatch_and_never_return_empty_success()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        var receipt=await new TrajectoryLearner(_=>new Client((_,_)=>Text("Done."))).LearnAsync(learn,CancellationToken.None);
        var request=Request(learn,receipt);int calls=0;
        var drift=new EvidenceRetriever(_=>new Client((_,_)=>{calls++;return Text("{\"references\":[]}");}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>drift.RetrieveAsync(request with {Retrieval=request.Retrieval with {CatalogHash=new string('e',64)}},CancellationToken.None));
        Assert.Equal(0,calls);File.Delete(request.Paths.EventLogPath);
        var mutation=new EvidenceRetriever(_=>new Client((_,_)=>new ChatResponse(new ChatMessage(ChatRole.Assistant,[new FunctionCallContent("mutation","remember",new Dictionary<string,object?> {["path"]="/derived/evil",["content"]="evil"})])) {ModelId="synthetic",FinishReason=ChatFinishReason.ToolCalls}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>mutation.RetrieveAsync(request,CancellationToken.None));
        var log=File.ReadAllText(request.Paths.EventLogPath);
        Assert.Contains("model-response",log);
        Assert.Contains("remember",log);
        Assert.Contains("/derived/evil",log);
        Assert.DoesNotContain("tool-intent",log);
        Assert.Equal(receipt.NewSnapshotHash,SnapshotValidator.Fingerprint(learn.StagingPath));
    }
    internal static RetrieveRequest Request(LearnRequest learn,LearningReceipt receipt)=>new() {
        Version=1,Operation="retrieve",OperationId=Guid.NewGuid().ToString("N"),Configuration=learn.Configuration,
        Paths=learn.Paths with {EventLogPath=Path.Combine(learn.Paths.Root,"retrieve.jsonl")},SnapshotPath=learn.StagingPath,
        ExpectedFingerprint=receipt.NewSnapshotHash,Question="Synthetic question?",QuestionImage=learn.Trajectory.States[0].Screenshot,
        Retrieval=new() {Executable=learn.Learning.Executable,PrefixArguments=learn.Learning.PrefixArguments,
            CatalogHash=learn.Learning.CatalogHash,InstructionsHash=SourceCatalog.Hash(Encoding.UTF8.GetBytes(RetrievalInstructions.Text))}};
    internal static string Selection(LearnRequest learn)=>"{\"references\":[{\"kind\":\"source\",\"sourceId\":\""+learn.Fragments[0].SourceId+"\",\"stateIndex\":0}]}";
    internal static ChatResponse Text(string value)=>new(new ChatMessage(ChatRole.Assistant,value)){ModelId="synthetic",FinishReason=ChatFinishReason.Stop};
    internal sealed class Client(Func<IEnumerable<ChatMessage>,ChatOptions?,ChatResponse> respond):IChatClient {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>Task.FromResult(respond(messages,options));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public object? GetService(Type type,object? key=null)=>null;
        public void Dispose(){}
    }
}
