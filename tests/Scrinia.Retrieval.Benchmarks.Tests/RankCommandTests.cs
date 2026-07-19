using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="RankCommand"/> — the ranking reader that sorts prior result files
/// by the judge <c>CorrectRate</c>/<c>SurfacedRate</c> metric (Task 1), tolerantly falling
/// back to the legacy <c>Answerable</c> field for result files that predate the schema
/// split. Result files in the real results dir run 25-180 MB (the <c>Questions</c> array
/// dominates), so <see cref="RankCommand.Summarize"/> must read only the leading
/// <c>RunMetadata</c>+<c>Aggregate</c> prefix — these tests include a large-<c>Questions</c>
/// fixture to prove that prefix-only read actually happens instead of loading the file.
/// </summary>
public sealed class RankCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private string WriteFixture(string suffix, string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rank_cmd_{Guid.NewGuid():N}_{suffix}");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    private const string LegacyJson = """
        {
          "RunMetadata": {
            "Benchmark": "locomo",
            "Mode": "bm25",
            "TopK": 50,
            "StartedAt": "2026-05-01T00:00:00Z",
            "CompletedAt": "2026-05-01T00:01:00Z",
            "QuestionCount": 10,
            "ScriniaVersion": "0.1.0"
          },
          "Aggregate": {
            "QuestionCount": 10,
            "RecallAt5": 0.5,
            "RecallAt10": 0.6,
            "RecallAt50": 0.7,
            "Ndcg10": 0.4,
            "Answerable": 0.42
          },
          "PerCategory": {},
          "Questions": []
        }
        """;

    private const string NewSchemaJson = """
        {
          "RunMetadata": {
            "Benchmark": "locomo",
            "Mode": "bm25_embeddings",
            "TopK": 50,
            "StartedAt": "2026-07-01T00:00:00Z",
            "CompletedAt": "2026-07-01T00:05:00Z",
            "QuestionCount": 20,
            "ScriniaVersion": "0.5.0",
            "Granularity": "session",
            "TemporalScenario": "session-date-ingest"
          },
          "Aggregate": {
            "QuestionCount": 20,
            "RecallAt5": 0.6,
            "RecallAt10": 0.7,
            "RecallAt50": 0.8,
            "Ndcg10": 0.5,
            "SurfacedRate": 0.9,
            "CorrectRate": 0.75
          },
          "PerCategory": {},
          "Questions": []
        }
        """;

    [Fact]
    public void Summarize_reads_new_and_legacy_and_sorts_by_correct_rate_desc()
    {
        string legacyPath = WriteFixture("legacy.json", LegacyJson);
        string newPath = WriteFixture("new.json", NewSchemaJson);

        var rows = RankCommand.Summarize([legacyPath, newPath]);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].CorrectRate >= rows[1].CorrectRate);
        var legacy = rows.Single(r => r.File.EndsWith("legacy.json"));
        Assert.Equal(0.42, legacy.CorrectRate, 3);
    }

    [Fact]
    public void Summarize_new_schema_row_carries_question_count_surfaced_rate_and_metadata()
    {
        string newPath = WriteFixture("new.json", NewSchemaJson);

        var rows = RankCommand.Summarize([newPath]);

        var row = rows.Single();
        row.QuestionCount.Should().Be(20);
        row.CorrectRate.Should().Be(0.75);
        row.SurfacedRate.Should().Be(0.9);
        row.Granularity.Should().Be("session");
        row.TemporalScenario.Should().Be("session-date-ingest");
    }

    [Fact]
    public void Summarize_legacy_row_has_null_granularity_and_temporal_scenario()
    {
        string legacyPath = WriteFixture("legacy.json", LegacyJson);

        var row = RankCommand.Summarize([legacyPath]).Single();

        row.Granularity.Should().BeNull();
        row.TemporalScenario.Should().BeNull();
        row.QuestionCount.Should().Be(10);
    }

    /// <summary>
    /// Proves the streaming reader stops before the <c>Questions</c> array rather than
    /// materializing it: a many-megabyte array here (well past this suite's normal fixture
    /// size) still summarizes quickly and correctly, because <see cref="RankCommand"/> never
    /// parses past the <c>Questions</c> property name.
    /// </summary>
    [Fact]
    public void Summarize_does_not_choke_on_a_huge_questions_array()
    {
        var hugeQuestions = new System.Text.StringBuilder();
        hugeQuestions.Append('[');
        for (int i = 0; i < 20_000; i++)
        {
            if (i > 0) hugeQuestions.Append(',');
            hugeQuestions.Append($$"""{"padding":"{{new string('x', 200)}}"}""");
        }
        hugeQuestions.Append(']');

        string json = $$"""
            {
              "RunMetadata": {
                "Benchmark": "locomo",
                "Mode": "bm25_embeddings",
                "TopK": 50,
                "StartedAt": "2026-07-01T00:00:00Z",
                "CompletedAt": "2026-07-01T00:05:00Z",
                "QuestionCount": 20000,
                "ScriniaVersion": "0.5.0",
                "Granularity": "dialog",
                "TemporalScenario": "session-date-ingest"
              },
              "Aggregate": {
                "QuestionCount": 20000,
                "RecallAt5": 0.6,
                "RecallAt10": 0.7,
                "RecallAt50": 0.8,
                "Ndcg10": 0.5,
                "SurfacedRate": 0.9,
                "CorrectRate": 0.75
              },
              "PerCategory": {},
              "Questions": {{hugeQuestions}}
            }
            """;
        string path = WriteFixture("huge.json", json);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var row = RankCommand.Summarize([path]).Single();
        sw.Stop();

        row.CorrectRate.Should().Be(0.75);
        row.QuestionCount.Should().Be(20000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Summarize_skips_a_file_that_cannot_be_read_without_throwing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"rank_cmd_missing_{Guid.NewGuid():N}.json");
        string newPath = WriteFixture("new.json", NewSchemaJson);

        var rows = RankCommand.Summarize([missingPath, newPath]);

        rows.Should().ContainSingle();
        rows[0].File.Should().Be(newPath);
    }

    [Fact]
    public async Task RunAsync_globs_dir_and_prints_a_table()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rank_cmd_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "scrinia_locomo_bm25_20260501_0000.json"), LegacyJson);
            File.WriteAllText(Path.Combine(dir, "scrinia_locomo_bm25_embeddings_20260701_0000.judged.json"), NewSchemaJson);
            File.WriteAllText(Path.Combine(dir, "not_a_result_file.json"), NewSchemaJson);

            var originalOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            int exitCode;
            try
            {
                exitCode = await RankCommand.RunAsync(["--dir", dir], CancellationToken.None);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            exitCode.Should().Be(0);
            string output = sw.ToString();
            output.Should().Contain("scrinia_locomo_bm25_20260501_0000.json");
            output.Should().Contain("scrinia_locomo_bm25_embeddings_20260701_0000.judged.json");
            output.Should().NotContain("not_a_result_file.json");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
