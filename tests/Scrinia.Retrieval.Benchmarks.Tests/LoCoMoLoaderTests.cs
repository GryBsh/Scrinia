using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies the LoCoMo loader parses the published <c>locomo10.json</c> shape correctly:
/// per-conversation grouping (one <see cref="BenchmarkQuestionGroup"/> per
/// <c>sample_id</c>), session-granularity haystack from concatenated dialogs,
/// dialog-granularity haystack with <c>D{N}:{M}</c> doc IDs, evidence dia_ids parsed
/// out of <c>{dia_id, text}</c> objects, ground-truth translation from dia_ids to
/// session IDs at session granularity.
/// </summary>
public class LoCoMoLoaderTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dataPath = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scrinia-locomo-loader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dataPath = Path.Combine(_dir, "locomo10.json");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Parses_published_locomo_session_timestamp_format()
    {
        // The real locomo10.json uses "h:mm tt on d MMMM, yyyy" (e.g. "1:56 pm on 8 May, 2023"),
        // which DateTimeOffset.TryParse rejects — leaving Timestamp null for every session and
        // silently disabling temporal grounding. Fixtures elsewhere in this file use a standard
        // "2024-01-15 10:00" shape that parses, so they never caught this. Assert the REAL format.
        const string json = """
        [
          {
            "sample_id": "conv_real",
            "conversation": {
              "session_1": [{"speaker": "Caroline", "text": "I went to a support group yesterday"}],
              "session_1_date_time": "1:56 pm on 8 May, 2023"
            },
            "qa": [{"question": "when?", "category": "temporal", "evidence": [{"dia_id": "D1:1"}]}]
          }
        ]
        """;
        await File.WriteAllTextAsync(_dataPath, json);

        var groups = new List<BenchmarkQuestionGroup>();
        var loader = new LoCoMoLoader(_dataPath, LoCoMoLoader.Granularity.Session);
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        var doc = groups.Single().SharedHaystack.Single();
        doc.Timestamp.Should().NotBeNull(because: "the published 'h:mm tt on d MMMM, yyyy' format must parse");
        doc.Timestamp!.Value.UtcDateTime.Date.Should().Be(new DateTime(2023, 5, 8));
    }

    private const string SampleJson = """
    [
      {
        "sample_id": "conv_a",
        "conversation": {
          "session_1": [
            {"speaker": "Alice", "text": "I just got back from Tokyo"},
            {"speaker": "Bob", "text": "Sounds amazing"}
          ],
          "session_1_date_time": "2024-01-15 10:00",
          "session_2": [
            {"speaker": "Alice", "text": "Started a new job at Megacorp last week"}
          ],
          "session_2_date_time": "2024-02-10 14:30"
        },
        "qa": [
          {
            "question": "Where did Alice travel?",
            "answer": "Tokyo",
            "category": "event",
            "evidence": [{"dia_id": "D1:1", "text": "I just got back from Tokyo"}]
          },
          {
            "question": "Where does Alice work?",
            "answer": "Megacorp",
            "category": "employment",
            "evidence": [{"dia_id": "D2:1", "text": "Started a new job at Megacorp last week"}]
          }
        ]
      },
      {
        "sample_id": "conv_b",
        "conversation": {
          "session_1": [{"speaker": "C", "text": "I love pizza"}],
          "session_1_date_time": "2024-03-01 12:00"
        },
        "qa": [
          {
            "question": "What food does C like?",
            "answer": "Pizza",
            "category": "preference",
            "evidence": [{"dia_id": "D1:1"}]
          }
        ]
      }
    ]
    """;

    [Fact]
    public async Task Session_granularity_groups_by_conversation_with_session_haystack()
    {
        await File.WriteAllTextAsync(_dataPath, SampleJson);

        var loader = new LoCoMoLoader(_dataPath, LoCoMoLoader.Granularity.Session);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        groups.Should().HaveCount(2);

        var convA = groups.Single(g => g.GroupId == "conv_a");
        convA.SharedHaystack.Should().HaveCount(2, because: "session_1 and session_2 each become one doc");
        convA.SharedHaystack[0].Id.Should().Be("session_1");
        convA.SharedHaystack[0].Text.Should().Contain("Tokyo");
        convA.SharedHaystack[0].Text.Should().Contain("Alice");
        convA.SharedHaystack[0].Timestamp.Should().NotBeNull();

        convA.Questions.Should().HaveCount(2);
        convA.Questions[0].GroundTruthIds.Should().ContainSingle().Which.Should().Be("session_1");
        convA.Questions[1].GroundTruthIds.Should().ContainSingle().Which.Should().Be("session_2");
        convA.Questions[0].Category.Should().Be("event");
    }

    [Fact]
    public async Task Dialog_granularity_uses_dia_ids_directly()
    {
        await File.WriteAllTextAsync(_dataPath, SampleJson);

        var loader = new LoCoMoLoader(_dataPath, LoCoMoLoader.Granularity.Dialog);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        var convA = groups.Single(g => g.GroupId == "conv_a");
        convA.SharedHaystack.Should().HaveCount(3,
            because: "session_1 has 2 dialogs + session_2 has 1");
        convA.SharedHaystack.Select(d => d.Id).Should().ContainInOrder("D1:1", "D1:2", "D2:1");
        convA.Questions[0].GroundTruthIds.Should().ContainSingle().Which.Should().Be("D1:1");
    }

    [Fact]
    public async Task Integer_category_codes_map_to_locomo_paper_labels()
    {
        // snap-research/locomo's locomo10.json ships QA category as an int 1-5 rather
        // than the string label the bench script expects. The loader must remap.
        const string json = """
        [
          {
            "sample_id": "conv_int_cats",
            "conversation": {
              "session_1": [{"speaker": "A", "text": "hi"}],
              "session_1_date_time": "2024-01-01"
            },
            "qa": [
              {"question": "q1", "category": 1, "evidence": ["D1:1"]},
              {"question": "q2", "category": 2, "evidence": ["D1:1"]},
              {"question": "q3", "category": 3, "evidence": ["D1:1"]},
              {"question": "q4", "category": 4, "evidence": ["D1:1"]},
              {"question": "q5", "category": 5, "evidence": ["D1:1"]},
              {"question": "q6", "category": 99, "evidence": ["D1:1"]}
            ]
          }
        ]
        """;
        await File.WriteAllTextAsync(_dataPath, json);

        var loader = new LoCoMoLoader(_dataPath);
        var cats = new List<string>();
        await foreach (var g in loader.LoadAsync()) cats.AddRange(g.Questions.Select(q => q.Category));

        cats.Should().Equal(
            ["single_hop", "temporal", "temporal_inference", "open_domain", "adversarial", "category_99"]);
    }

    [Fact]
    public async Task Multiple_evidence_dia_ids_dedupe_to_session_ids()
    {
        const string json = """
        [
          {
            "sample_id": "conv_x",
            "conversation": {
              "session_1": [{"speaker": "A", "text": "first"}, {"speaker": "A", "text": "second"}],
              "session_1_date_time": "2024-01-01",
              "session_2": [{"speaker": "B", "text": "third"}],
              "session_2_date_time": "2024-01-02"
            },
            "qa": [
              {
                "question": "?",
                "category": "x",
                "evidence": [
                  {"dia_id": "D1:1"},
                  {"dia_id": "D1:2"},
                  {"dia_id": "D2:1"}
                ]
              }
            ]
          }
        ]
        """;
        await File.WriteAllTextAsync(_dataPath, json);

        var loader = new LoCoMoLoader(_dataPath, LoCoMoLoader.Granularity.Session);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        groups[0].Questions[0].GroundTruthIds.Should().BeEquivalentTo(new[] { "session_1", "session_2" });
    }

    [Fact]
    public async Task Conversations_without_qa_or_haystack_are_skipped()
    {
        const string json = """
        [
          {"sample_id": "no-qa", "conversation": {"session_1": [{"speaker": "x", "text": "hi"}], "session_1_date_time": "2024-01-01"}, "qa": []},
          {"sample_id": "no-stack", "conversation": {}, "qa": [{"question": "q", "evidence": [{"dia_id": "D1:1"}]}]},
          {"sample_id": "ok", "conversation": {"session_1": [{"speaker": "x", "text": "hi"}], "session_1_date_time": "2024-01-01"}, "qa": [{"question": "q", "category": "c", "evidence": [{"dia_id": "D1:1"}]}]}
        ]
        """;
        await File.WriteAllTextAsync(_dataPath, json);

        var loader = new LoCoMoLoader(_dataPath);
        var groups = new List<BenchmarkQuestionGroup>();
        await foreach (var g in loader.LoadAsync()) groups.Add(g);

        groups.Should().ContainSingle().Which.GroupId.Should().Be("ok");
    }
}
