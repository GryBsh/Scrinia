using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class EpisodeCacheTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"epcache_{Guid.NewGuid():N}.ndjson");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    [Fact]
    public void RoundTrips_ToolCallSequence()
    {
        var cache = new EpisodeCache(_file);
        string key = EpisodeCache.Key("qwen3", "v1", "transcript text");
        cache.TryGet(key, out _).Should().BeFalse();

        var calls = new List<EpisodeToolCall> { new("memory_remember", """{"path":"/f/x","content":["a\nref: d1"]}""") };
        cache.Put(key, calls);

        var reload = new EpisodeCache(_file);
        reload.TryGet(key, out var got).Should().BeTrue();
        got.Should().HaveCount(1);
        got[0].ToolName.Should().Be("memory_remember");
    }

    [Fact]
    public void Key_IsStable_ForSameInputs_AndVariesByPromptVersion()
    {
        EpisodeCache.Key("m", "v1", "t").Should().Be(EpisodeCache.Key("m", "v1", "t"));
        EpisodeCache.Key("m", "v1", "t").Should().NotBe(EpisodeCache.Key("m", "v2", "t"));
    }
}
