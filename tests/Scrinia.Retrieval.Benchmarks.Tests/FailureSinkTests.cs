using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Llm;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class FailureSinkTests
{
    [Fact]
    public void PathForCache_DerivesSiblingFailuresFile()
    {
        string cache = Path.Combine("cache", "agent-openai-qwen3-2.ndjson");
        FailureSink.PathForCache(cache)
            .Should().Be(Path.Combine("cache", "agent-openai-qwen3-2.failures.ndjson"));
    }

    [Fact]
    public void Construction_TruncatesStaleFile_ThenRecordAppends()
    {
        // Unique path per test so the once-per-process truncation guard fires here.
        string path = Path.Combine(Path.GetTempPath(), $"fs-{Guid.NewGuid():N}.failures.ndjson");
        File.WriteAllText(path, "STALE LINE FROM A PRIOR RUN\n");
        try
        {
            var sink = new FailureSink(path);
            File.ReadAllText(path).Should().BeEmpty("the failures file is cleared at run start");

            sink.Record(new FailureRecord(
                Role: "ingest", Id: "h1", Reason: "empty-completion",
                Attempts: 1, MaxTokens: 512, PromptChars: 8,
                SystemPrompt: "sys", UserPrompt: "user",
                AttemptLog: new[] { new FailureAttempt(1, null, null) }));
            sink.Record(new FailureRecord(
                Role: "grader", Id: "conv-44_qa1", Reason: "empty-completion",
                Attempts: 3, MaxTokens: 2048, PromptChars: 12,
                SystemPrompt: "sys", UserPrompt: "user",
                AttemptLog: new[]
                {
                    new FailureAttempt(1, "", null),
                    new FailureAttempt(2, "", null),
                    new FailureAttempt(3, "", null),
                }));

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(2, "appends accumulate, they don't clobber");

            using var doc = JsonDocument.Parse(lines[0]);
            doc.RootElement.GetProperty("role").GetString().Should().Be("ingest");
            doc.RootElement.GetProperty("reason").GetString().Should().Be("empty-completion");
            doc.RootElement.GetProperty("attemptLog").GetArrayLength().Should().Be(1);

            using var doc2 = JsonDocument.Parse(lines[1]);
            doc2.RootElement.GetProperty("id").GetString().Should().Be("conv-44_qa1");
            doc2.RootElement.GetProperty("attemptLog").GetArrayLength().Should().Be(3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
