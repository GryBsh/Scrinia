using System.Text;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Retrieval;

public sealed class EvidenceRetriever(Func<ModelSettings,IChatClient>? createClient=null)
{
    public async Task<EvidenceSelection> RetrieveAsync(RetrieveRequest request,CancellationToken ct)
        =>(await RetrieveEvidenceAsync(request,ct)).Selection;
    public async Task<(EvidenceSelection Selection,EvidenceItem[] Items)> RetrieveEvidenceAsync(RetrieveRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();WorkerProtocol.Validate(request);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.Configuration.EpisodeSeconds));ct=deadline.Token;
        await new SnapshotValidator().ValidateAsync(request.SnapshotPath,request.ExpectedFingerprint,ct);
        var catalogs=SourceCatalog.ReadAll(request.SnapshotPath);
        if(request.Retrieval.InstructionsHash!=SourceCatalog.Hash(Encoding.UTF8.GetBytes(RetrievalInstructions.Text)))
            throw new InvalidDataException("RETRIEVAL_INSTRUCTIONS_MISMATCH");
        List<AIContent> content=[new TextContent(request.Question)];
        if(request.QuestionImage is {} image)content.Add(new DataContent(await EvidenceMaterializer.VerifyImageAsync(image.Path,image,ct),image.MediaType));
        await using var events=new LearningEvents(request.Paths.EventLogPath);
        var settings=new ScriniaSettings(Path.GetDirectoryName(request.Retrieval.Executable)!,request.Retrieval.Executable,request.Retrieval.PrefixArguments,[],null);
        var paths=new AttemptPaths(request.Paths.Root,request.Paths.Workspace,request.Paths.Home,request.Paths.EventLogPath);
        EvidenceSelection selection;EvidenceItem[] items;
        await using(var session=await ScriniaMcpSession.OpenAsync(settings,paths,events,ct)) {
            if(session.CatalogHash!=request.Retrieval.CatalogHash)throw new InvalidDataException("RETRIEVAL_CATALOG_MISMATCH");
            var config=request.Configuration;
            var model=new ModelSettings(new Uri(config.Endpoint),config.Model,config.ApiKeyEnvironmentVariable,(float)config.Temperature,config.MaxOutputTokens,(float)config.TopP,config.TopK);
            var result=await new RuntimeAgentEpisode(createClient??ModelClientFactory.Create,expectedModelId:request.Configuration.Model).RunAsync(
                new("retrieve",RetrievalInstructions.Text,[new ChatMessage(ChatRole.User,content)],RetrievalInstructions.AllowedTools,
                    EnableResponseRecovery:true,EnableThinking:config.Thinking,ValidateToolCall:RetrievalInstructions.Guard,ValidateResponse:RetrievalInstructions.GuardResponse),
                session,model,new(config.MaxModelRequests,config.EpisodeSeconds,config.EpisodeSeconds,config.MaxAttempts),events,ct);
            if(result.ToolErrors!=0)throw new InvalidDataException("RETRIEVAL_TOOL_ERROR");
            selection=EvidenceSelection.Parse(result.FinalText);selection.Validate(catalogs);
            items=await new EvidenceMaterializer(request.SnapshotPath).MaterializeAsync(selection,catalogs,session,ct);
        }
        ct.ThrowIfCancellationRequested();
        if(SnapshotValidator.Fingerprint(request.SnapshotPath)!=request.ExpectedFingerprint)throw new InvalidDataException("QUERY_COPY_CHANGED");
        return (selection,items);
    }
}
