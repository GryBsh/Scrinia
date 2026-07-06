using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class HaystackSessionGroupingTests
{
    [Fact]
    public void HaystackDoc_SessionId_DefaultsToNull()
    {
        var doc = new HaystackDoc("d1", "text", Timestamp: null);
        doc.SessionId.Should().BeNull("session granularity docs are their own episode");
    }

    [Fact]
    public void HaystackDoc_SessionId_CanBeSet()
    {
        var doc = new HaystackDoc("conv5_sess3_msg2", "text", Timestamp: null, SessionId: "conv5_sess3");
        doc.SessionId.Should().Be("conv5_sess3");
    }

    private const string LoCoMoSampleJson = """
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
          }
        ]
      }
    ]
    """;

    [Fact]
    public async Task LoCoMo_dialog_granularity_docs_carry_parent_session_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-locomo-session-grouping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dataPath = Path.Combine(dir, "locomo10.json");
            await File.WriteAllTextAsync(dataPath, LoCoMoSampleJson);

            var loader = new LoCoMoLoader(dataPath, LoCoMoLoader.Granularity.Dialog);
            var groups = new List<BenchmarkQuestionGroup>();
            await foreach (var g in loader.LoadAsync()) groups.Add(g);

            var convA = groups.Single(g => g.GroupId == "conv_a");
            convA.SharedHaystack.Should().OnlyContain(d => d.SessionId != null,
                because: "dialog granularity docs are sub-session slices");
            convA.SharedHaystack.Single(d => d.Id == "D1:1").SessionId.Should().Be("session_1");
            convA.SharedHaystack.Single(d => d.Id == "D1:2").SessionId.Should().Be("session_1");
            convA.SharedHaystack.Single(d => d.Id == "D2:1").SessionId.Should().Be("session_2");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoCoMo_session_granularity_docs_have_null_session_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-locomo-session-grouping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dataPath = Path.Combine(dir, "locomo10.json");
            await File.WriteAllTextAsync(dataPath, LoCoMoSampleJson);

            var loader = new LoCoMoLoader(dataPath, LoCoMoLoader.Granularity.Session);
            var groups = new List<BenchmarkQuestionGroup>();
            await foreach (var g in loader.LoadAsync()) groups.Add(g);

            var convA = groups.Single(g => g.GroupId == "conv_a");
            convA.SharedHaystack.Should().OnlyContain(d => d.SessionId == null,
                because: "session granularity docs already are whole sessions");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private const string LongMemEvalSampleJson = """
    [
      {
        "question_id": "q1",
        "question_type": "temporal-reasoning",
        "question": "What did I say about coffee?",
        "answer": "pour-over",
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

    [Fact]
    public async Task LongMemEval_turn_granularity_docs_carry_parent_session_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-lme-session-grouping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dataPath = Path.Combine(dir, "lme.json");
            await File.WriteAllTextAsync(dataPath, LongMemEvalSampleJson);

            var loader = new LongMemEvalLoader(dataPath, granularity: LongMemEvalLoader.Granularity.Turn);
            var qs = new List<BenchmarkQuestion>();
            await foreach (var q in loader.LoadAsync()) qs.Add(q);

            qs.Should().ContainSingle().Which.Haystack.Should().OnlyContain(d => d.SessionId == "s1",
                because: "turn granularity docs are sub-session slices belonging to session s1");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LongMemEval_session_granularity_docs_have_null_session_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-lme-session-grouping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string dataPath = Path.Combine(dir, "lme.json");
            await File.WriteAllTextAsync(dataPath, LongMemEvalSampleJson);

            var loader = new LongMemEvalLoader(dataPath);
            var qs = new List<BenchmarkQuestion>();
            await foreach (var q in loader.LoadAsync()) qs.Add(q);

            qs.Should().ContainSingle().Which.Haystack.Should().OnlyContain(d => d.SessionId == null,
                because: "session granularity docs already are whole sessions");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
