using System.Text;
using Microsoft.Extensions.AI;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
using Scrinia.LongMemEvalV2.Worker.Retrieval;
namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class EvidenceMaterializerTests
{
    [Fact]
    public async Task Missing_or_mutated_images_and_traversal_are_rejected()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();
        var learn=await fixture.RequestAsync();
        var image=learn.Trajectory.States[0].Screenshot;
        await File.WriteAllTextAsync(image.Path,"mutated");
        await Assert.ThrowsAsync<InvalidDataException>(()=>EvidenceMaterializer.VerifyImageAsync(image.Path,image,CancellationToken.None));
        File.Delete(image.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>EvidenceMaterializer.VerifyImageAsync(image.Path,image,CancellationToken.None));
        var catalog=new SourceCatalog {Trajectory=learn.Trajectory,Fragments=learn.Fragments};
        Assert.ThrowsAny<Exception>(()=>catalog.ResolveImage(learn.StagingPath,image with {Path="../escape.png"}));
        // A normalized path back into the generation is still not a canonical source path.
        Assert.ThrowsAny<Exception>(()=>EvidenceMaterializer.RequireSourceImagePath(catalog,learn.StagingPath,image with {Path="sources/../home/asset.png"}));
        var outside=Path.Combine(fixture.Root,"outside");Directory.CreateDirectory(outside);
        var link=Path.Combine(learn.StagingPath,"sources","escape");
        if(OperatingSystem.IsWindows()) {
            using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") {
                ArgumentList={"/c","mklink","/J",link,outside},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true});
            await process!.WaitForExitAsync();Assert.Equal(0,process.ExitCode);
        } else Directory.CreateSymbolicLink(link,outside);
        try {Assert.ThrowsAny<Exception>(()=>catalog.ResolveImage(learn.StagingPath,image with {Path="sources/escape/file.png"}));}
        finally {Directory.Delete(link);}
    }
    [Fact]
    public async Task Selected_memory_is_actually_recalled_and_fabricated_chunk_fails()
    {
        using var fixture=new LearningIntegrationTests.LearningFixture();var learn=await fixture.RequestAsync();
        var receipt=await new TrajectoryLearner(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text("Done."))).LearnAsync(learn,CancellationToken.None);
        string path=SourceCatalog.SourcePath(learn.Trajectory.SourceHash,learn.Fragments[0]);
        var request=RetrievalTests.Request(learn,receipt);
        var result=await new EvidenceRetriever(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text("{\"references\":[{\"kind\":\"memory\",\"path\":\""+path+"\",\"chunks\":[1]}]}"))).RetrieveEvidenceAsync(request,CancellationToken.None);
        Assert.Contains("2026-03-04",Assert.Single(result.Items).Value);
        File.Delete(request.Paths.EventLogPath);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new EvidenceRetriever(_=>new RetrievalTests.Client((_,_)=>RetrievalTests.Text("{\"references\":[{\"kind\":\"memory\",\"path\":\""+path+"\",\"chunks\":[2]}]}"))).RetrieveEvidenceAsync(request,CancellationToken.None));
    }
}
