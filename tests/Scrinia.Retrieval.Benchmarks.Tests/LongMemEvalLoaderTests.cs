using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies the LongMemEval loader parses the published HuggingFace dataset shape
/// correctly: per-question haystacks, user-turn-only session docs, ground-truth
/// session IDs preserved, question_type used as category. Split-file filtering
/// (<c>dev</c> / <c>held_out</c>) is also covered.
/// </summary>
public class LongMemEvalLoaderTests : IAsyncLifetime
{
    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scrinia-lme-loader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Parses_records_into_session_granularity_questions()
    {
        const string json = """
        [
          {
            "question_id": "q1",
            "question_type": "temporal-reasoning",
            "question": "What degree did I get?",
            "question_date": "2024-06-15",
            "answer": "Business Administration",
            "answer_session_ids": ["s2"],
            "haystack_sessions": [
              [{"role": "user", "content": "I love sushi"}, {"role": "assistant", "content": "Sushi is great"}],
              [{"role": "user", "content": "I graduated with a BA in business"}, {"role": "user", "content": "It was in 2018"}]
            ],
            "haystack_session_ids": ["s1", "s2"],
            "haystack_dates": ["2024-01-01", "2024-03-01"]
          },
          {
            "question_id": "q2",
            "question_type": "knowledge-update",
            "question": "Where do I work now?",
            "question_date": "2024-07-01",
            "answer": "Megacorp",
            "answer_session_ids": ["s3"],
            "haystack_sessions": [
              [{"role": "user", "content": "Started new job at Megacorp"}]
            ],
            "haystack_session_ids": ["s3"],
            "haystack_dates": ["2024-06-30"]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "longmemeval_s.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LongMemEvalLoader(dataPath);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(2);
        qs[0].Id.Should().Be("q1");
        qs[0].Category.Should().Be("temporal-reasoning");
        qs[0].Haystack.Should().HaveCount(2);
        // Session granularity = joined user turns, no assistant text.
        qs[0].Haystack[0].Text.Should().Be("I love sushi");
        qs[0].Haystack[1].Text.Should().Be("I graduated with a BA in business\nIt was in 2018");
        qs[0].GroundTruthIds.Should().ContainSingle().Which.Should().Be("s2");
        qs[1].Category.Should().Be("knowledge-update");
    }

    [Fact]
    public async Task Turn_granularity_expands_session_ids_into_turn_ids()
    {
        const string json = """
        [
          {
            "question_id": "q1",
            "question_type": "single-session-user",
            "question": "What did I say about coffee?",
            "answer": "I prefer pour-over",
            "answer_session_ids": ["s1"],
            "haystack_sessions": [
              [
                {"role": "user", "content": "I prefer pour-over coffee"},
                {"role": "assistant", "content": "Pour-over is great"},
                {"role": "user", "content": "I drink it black"}
              ]
            ],
            "haystack_session_ids": ["s1"],
            "haystack_dates": ["2024-01-01"]
          }
        ]
        """;
        string dataPath = Path.Combine(_dir, "lme.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LongMemEvalLoader(dataPath, granularity: LongMemEvalLoader.Granularity.Turn);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(1);
        qs[0].Haystack.Should().HaveCount(2,
            because: "only user turns become docs (assistant turn skipped)");
        qs[0].Haystack[0].Id.Should().Be("s1_turn_0");
        qs[0].Haystack[1].Id.Should().Be("s1_turn_1");
        qs[0].GroundTruthIds.Should().BeEquivalentTo(new[] { "s1_turn_0", "s1_turn_1" });
    }

    [Fact]
    public async Task Split_file_filters_to_held_out_question_ids()
    {
        const string lmeJson = """
        [
          {"question_id": "q1", "question_type": "t1", "question": "Q1", "answer_session_ids": ["s1"], "haystack_sessions": [[{"role": "user", "content": "x"}]], "haystack_session_ids": ["s1"], "haystack_dates": ["2024-01-01"]},
          {"question_id": "q2", "question_type": "t1", "question": "Q2", "answer_session_ids": ["s2"], "haystack_sessions": [[{"role": "user", "content": "x"}]], "haystack_session_ids": ["s2"], "haystack_dates": ["2024-01-02"]},
          {"question_id": "q3", "question_type": "t1", "question": "Q3", "answer_session_ids": ["s3"], "haystack_sessions": [[{"role": "user", "content": "x"}]], "haystack_session_ids": ["s3"], "haystack_dates": ["2024-01-03"]}
        ]
        """;
        const string splitJson = """
        {
          "dev": ["q1"],
          "held_out": ["q2", "q3"]
        }
        """;
        string dataPath = Path.Combine(_dir, "lme.json");
        string splitPath = Path.Combine(_dir, "lme_split_50_450.json");
        await File.WriteAllTextAsync(dataPath, lmeJson);
        await File.WriteAllTextAsync(splitPath, splitJson);

        var loader = new LongMemEvalLoader(dataPath, LongMemEvalLoader.SplitMode.HeldOut, splitPath);
        var ids = new List<string>();
        await foreach (var q in loader.LoadAsync()) ids.Add(q.Id);

        ids.Should().BeEquivalentTo(new[] { "q2", "q3" });
    }

    [Fact]
    public async Task Records_without_ground_truth_or_haystack_are_skipped()
    {
        const string json = """
        [
          {"question_id": "ok", "question_type": "t", "question": "ok", "answer_session_ids": ["s1"], "haystack_sessions": [[{"role": "user", "content": "x"}]], "haystack_session_ids": ["s1"], "haystack_dates": ["2024-01-01"]},
          {"question_id": "no-gt", "question_type": "t", "question": "no-gt", "answer_session_ids": [], "haystack_sessions": [[{"role": "user", "content": "x"}]], "haystack_session_ids": ["s2"], "haystack_dates": ["2024-01-01"]},
          {"question_id": "no-stack", "question_type": "t", "question": "no-stack", "answer_session_ids": ["s3"], "haystack_sessions": [], "haystack_session_ids": [], "haystack_dates": []}
        ]
        """;
        string dataPath = Path.Combine(_dir, "lme.json");
        await File.WriteAllTextAsync(dataPath, json);

        var loader = new LongMemEvalLoader(dataPath);
        var ids = new List<string>();
        await foreach (var q in loader.LoadAsync()) ids.Add(q.Id);

        ids.Should().ContainSingle().Which.Should().Be("ok");
    }
}
