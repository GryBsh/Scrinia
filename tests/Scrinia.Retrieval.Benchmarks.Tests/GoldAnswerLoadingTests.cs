using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class GoldAnswerLoadingTests : IAsyncLifetime
{
    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scrinia-gold-answer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void BenchmarkQuestion_GoldAnswer_DefaultsToNull()
    {
        var q = new BenchmarkQuestion("id", "q?", [], [], [], "cat");
        q.GoldAnswer.Should().BeNull();
    }

    [Fact]
    public void BenchmarkQuestion_GoldAnswer_CanBeSet()
    {
        var q = new BenchmarkQuestion("id", "q?", [], [], [], "cat", GoldAnswer: "42");
        q.GoldAnswer.Should().Be("42");
    }

    [Fact]
    public async Task LongMemEvalLoader_StringAnswer_YieldsGoldAnswer()
    {
        const string json = """
        [
          {
            "question_id": "q1",
            "question_type": "single-session-user",
            "question": "Where did I graduate from?",
            "answer": "Paris",
            "answer_session_ids": ["s1"],
            "haystack_sessions": [
              [{"role": "user", "content": "I studied in Paris"}]
            ],
            "haystack_session_ids": ["s1"],
            "haystack_dates": ["2024-01-01"]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "lme.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LongMemEvalLoader(dataPath);
        var qs = new List<BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().ContainSingle().Which.GoldAnswer.Should().Be("Paris");
    }

    [Fact]
    public async Task LongMemEvalLoader_ListAnswer_YieldsGoldAnswerContainingAllEntries()
    {
        const string json = """
        [
          {
            "question_id": "q1",
            "question_type": "knowledge-update",
            "question": "Where do I work?",
            "answer": ["a", "b"],
            "answer_session_ids": ["s1"],
            "haystack_sessions": [
              [{"role": "user", "content": "I work at a place"}]
            ],
            "haystack_session_ids": ["s1"],
            "haystack_dates": ["2024-01-01"]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "lme.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LongMemEvalLoader(dataPath);
        var qs = new List<BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().ContainSingle().Which.GoldAnswer.Should().Be("acceptable answers: a; b");
    }

    [Fact]
    public async Task LoCoMoLoader_NumericAnswer_YieldsGoldAnswer()
    {
        const string json = """
        [
          {
            "sample_id": "conv_a",
            "conversation": {
              "session_1": [{"speaker": "Alice", "text": "I have 5 cats"}],
              "session_1_date_time": "2024-01-15 10:00"
            },
            "qa": [
              {
                "question": "How many cats does Alice have?",
                "answer": 5,
                "category": "count",
                "evidence": [{"dia_id": "D1:1", "text": "I have 5 cats"}]
              }
            ]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "locomo10.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LoCoMoLoader(dataPath);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        groups.Should().ContainSingle();
        groups[0].Questions.Should().ContainSingle().Which.GoldAnswer.Should().Be("5");
    }

    [Fact]
    public async Task LoCoMoLoader_BooleanAnswer_YieldsGoldAnswer()
    {
        const string json = """
        [
          {
            "sample_id": "conv_a",
            "conversation": {
              "session_1": [{"speaker": "Alice", "text": "I own a cat"}],
              "session_1_date_time": "2024-01-15 10:00"
            },
            "qa": [
              {
                "question": "Does Alice own a cat?",
                "answer": true,
                "category": "count",
                "evidence": [{"dia_id": "D1:1", "text": "I own a cat"}]
              }
            ]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "locomo10.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LoCoMoLoader(dataPath);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        groups.Should().ContainSingle();
        groups[0].Questions.Should().ContainSingle().Which.GoldAnswer.Should().Be("true");
    }
}
