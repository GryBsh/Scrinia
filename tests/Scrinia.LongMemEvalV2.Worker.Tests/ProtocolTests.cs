using System.Text.Json;
using Scrinia.LongMemEvalV2.Worker;
using Scrinia.LongMemEvalV2.Worker.Protocol;

namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class ProtocolTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-retrieve.json"))
        .Replace("C:/lme-fixture", Path.GetTempPath().Replace("\\", "/").TrimEnd('/') + "/lme-fixture", StringComparison.Ordinal);

    [Fact]
    public void Source_generated_roundtrip_preserves_shared_fixture()
    {
        var request = Assert.IsType<RetrieveRequest>(WorkerProtocol.Parse(Fixture));
        Assert.Equal("Synthetic question?", request.Question);
        Assert.Equal("SCRINIA_TEST_KEY", request.Configuration.ApiKeyEnvironmentVariable);
        var serialized = JsonSerializer.Serialize(request, WorkerJsonContext.Default.RetrieveRequest);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(Fixture).RootElement, JsonDocument.Parse(serialized).RootElement));
    }

    [Fact]
    public void Python_generated_learning_fixture_preserves_canonical_hash_and_utf8_offsets()
    {
        var json=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"protocol-learn.json"))
            .Replace("C:/lme-fixture",Path.GetTempPath().Replace("\\","/").TrimEnd('/')+"/lme-fixture",StringComparison.Ordinal);
        var request=Assert.IsType<LearnRequest>(WorkerProtocol.Parse(json));
        Assert.Equal(request.Trajectory.SourceHash,Evidence.SourceCatalog.LogicalHash(request.Trajectory));
        Evidence.SourceCatalog.ValidateFragments(request.Trajectory,request.Fragments);
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2")]
    [InlineData("\"operation\": \"retrieve\"", "\"operation\": \"grade\"")]
    [InlineData("\"question\": \"Synthetic question?\"", "\"goldAnswer\": \"forbidden\"")]
    [InlineData("\"thinking\": true", "\"thinking\": true, \"apiKey\": \"sentinel-secret\"")]
    [InlineData("\"version\": 1", "\"version\": 1, \"version\": 1")]
    [InlineData("\"questionImage\": null", "\"questionImage\": {\"path\": \"https://example.invalid/a.png\", \"sha256\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\", \"mediaType\": \"image/png\", \"byteLength\": 1}")]
    public void Rejects_unsupported_or_leaking_protocol(string before, string after)
        => Assert.ThrowsAny<Exception>(() => WorkerProtocol.Parse(Fixture.Replace(before, after, StringComparison.Ordinal)));

    [Theory]
    [InlineData("{\"operation\":\"cancel\"}")]
    [InlineData("")]
    public async Task Control_line_or_EOF_cancels_and_disposes_owned_operation(string control)
    {
        var request = WorkerProtocol.Parse(Fixture);
        var operation = new BlockingOperation();
        var result = await WorkerHost.RunWithControlAsync(request, new StringReader(control), operation, CancellationToken.None);
        Assert.True(operation.Disposed);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal("CANCELLED", result.ErrorCode);
    }

    [Fact]
    public async Task Scaffold_refuses_real_operations()
    {
        var result = await WorkerHost.ExecuteOwnedAsync(WorkerProtocol.Parse(Fixture),
            new UnsupportedOperation(), CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Equal("UNSUPPORTED_OPERATION", result.ErrorCode);
        Assert.Null(result.Evidence);
        Assert.Null(result.Receipt);
    }

    private sealed class BlockingOperation : IWorkerOperation
    {
        public bool Disposed { get; private set; }
        public async Task<WorkerResult> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
