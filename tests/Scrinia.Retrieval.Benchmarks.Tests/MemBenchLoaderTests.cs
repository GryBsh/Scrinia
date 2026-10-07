using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies MemBench JSON parsing: per-file topic-keyed root dict, per-item haystack
/// from <c>message_list</c>, doc IDs = turn <c>sid</c>, ground-truth IDs from
/// <c>QA.target_step_id</c> (first element of each inner list), category named after
/// the source filename.
/// </summary>
public class MemBenchLoaderTests : IAsyncLifetime
{
    private static readonly string[] Expected13 = new[] { "1", "3" };
    private static readonly string[] Expected1718 = new[] { "17", "18" };
    private static readonly string[] Expected4918 = new[] { "4", "9", "18" };

    private string _root = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "scrinia-membench-loader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Parses_topic_keyed_categories_into_questions()
    {
        const string json = """
        {
          "movie": [
            {
              "tid": 1,
              "message_list": [
                [
                  {"sid": 1, "user": "I love action films", "assistant": "Got it", "time": "2024-01-15 10:00"},
                  {"sid": 2, "user": "Especially Bond movies", "assistant": "Classic"}
                ],
                [
                  {"sid": 3, "user": "Watched another last night", "assistant": "How was it?"}
                ]
              ],
              "QA": {
                "question": "What movies do I like?",
                "answer": "Action films",
                "target_step_id": [[1], [3]]
              }
            }
          ],
          "food": [
            {
              "tid": 1,
              "message_list": [
                [{"sid": 1, "user": "Pizza is my favorite"}]
              ],
              "QA": {
                "question": "What food do I prefer?",
                "answer": "Pizza",
                "target_step_id": [[1]]
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "simple.json"), json);

        var loader = new MemBenchLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(2);
        var movieQ = qs.Single(q => q.Id.Contains("movie", StringComparison.Ordinal));
        movieQ.Category.Should().Be("simple");
        movieQ.QueryText.Should().Be("What movies do I like?");
        movieQ.Haystack.Should().HaveCount(3, because: "all turns across both sessions");
        movieQ.Haystack[0].Id.Should().Be("1");
        movieQ.Haystack[0].Text.Should().Contain("2024-01-15");
        movieQ.Haystack[0].Text.Should().Contain("[User]");
        movieQ.GroundTruthIds.Should().BeEquivalentTo(Expected13);
    }

    [Fact]
    public async Task Items_with_no_target_step_id_are_skipped()
    {
        const string json = """
        {
          "movie": [
            {"tid": 1, "message_list": [[{"sid": 1, "user": "hi"}]], "QA": {"question": "q", "target_step_id": []}},
            {"tid": 2, "message_list": [[{"sid": 1, "user": "hi"}]], "QA": {"question": "q", "target_step_id": [[1]]}}
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "noisy.json"), json);

        var loader = new MemBenchLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(1);
        qs[0].Id.Should().EndWith("_2");
    }

    [Fact]
    public async Task Parses_roles_schema_with_sid_and_user_message_assistant_message()
    {
        // Matches the real shape used by simple/noisy/aggregative/comparative/conditional/
        // knowledge_update/post_processing.json files. Earlier loader version only
        // mapped `user`/`assistant`, so docs from these categories were timestamp-only
        // and retrieval signal collapsed.
        const string json = """
        {
          "roles": [
            {
              "tid": 1,
              "message_list": [
                [
                  {"sid": 17, "user_message": "I want to talk about my brother Ethan.", "assistant_message": "Sure!", "time": "2024-10-01 08:00"},
                  {"sid": 18, "user_message": "He's 28 years old.", "assistant_message": "Got it."}
                ]
              ],
              "QA": {
                "question": "How old is Ethan?",
                "target_step_id": [[17], [18]]
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "aggregative.json"), json);

        var loader = new MemBenchLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var item in loader.LoadAsync()) qs.Add(item);

        qs.Should().HaveCount(1);
        var question = qs[0];
        question.Category.Should().Be("aggregative");
        question.Haystack.Should().HaveCount(2);
        question.Haystack[0].Id.Should().Be("17");
        question.Haystack[0].Text.Should().Contain("[User] I want to talk about my brother Ethan.");
        question.Haystack[0].Text.Should().Contain("[Assistant] Sure!");
        question.Haystack[1].Id.Should().Be("18");
        question.Haystack[1].Text.Should().Contain("[User] He's 28 years old.");
        question.GroundTruthIds.Should().BeEquivalentTo(Expected1718);
    }

    [Fact]
    public async Task Parses_multi_agent_schema_with_mid_and_user_assistant()
    {
        // Matches the real shape used by RecMultiSession.json (top key
        // `multi_agent`) and the movie-style files (highlevel, highlevel_rec,
        // lowlevel_rec). Earlier loader version mapped only `sid`, so every turn
        // collapsed to doc id 0 and ground-truth ids like 4/9/18 never matched
        // anything in the haystack.
        const string json = """
        {
          "multi_agent": [
            {
              "tid": 1,
              "message_list": [
                [
                  {"mid": 4, "user": "I'm looking for a movie.", "assistant": "Try Alien (1979).", "time": "2024-10-01 08:04"},
                  {"mid": 9, "user": "Something else?", "assistant": "The Fugitive (1993)."}
                ],
                [
                  {"mid": 18, "user": "A food I should try?", "assistant": "Apple Pie."}
                ]
              ],
              "QA": {
                "question": "What have you recommended?",
                "target_step_id": [[4, 0], [9, 0], [18, 1]]
              }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "RecMultiSession.json"), json);

        var loader = new MemBenchLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var item in loader.LoadAsync()) qs.Add(item);

        qs.Should().HaveCount(1);
        var question = qs[0];
        question.Category.Should().Be("RecMultiSession");
        question.Haystack.Should().HaveCount(3);
        question.Haystack.Select(d => d.Id).Should().BeEquivalentTo(Expected4918,
            because: "every turn must have a distinct id; defaulting to 0 collapses the whole haystack");
        question.Haystack[0].Text.Should().Contain("[User] I'm looking for a movie.");
        question.Haystack[0].Text.Should().Contain("[Assistant] Try Alien (1979).");
        question.GroundTruthIds.Should().BeEquivalentTo(Expected4918);
    }

    [Fact]
    public async Task Per_category_limit_caps_questions_per_file()
    {
        const string json = """
        {
          "movie": [
            {"tid": 1, "message_list": [[{"sid": 1, "user": "a"}]], "QA": {"question": "q1", "target_step_id": [[1]]}},
            {"tid": 2, "message_list": [[{"sid": 1, "user": "b"}]], "QA": {"question": "q2", "target_step_id": [[1]]}}
          ],
          "food": [
            {"tid": 1, "message_list": [[{"sid": 1, "user": "c"}]], "QA": {"question": "q3", "target_step_id": [[1]]}}
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "highlevel.json"), json);

        var loader = new MemBenchLoader(_root, perCategoryLimit: 2);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        // Per-category (file) limit of 2: takes 2 from movie, then short-circuits before food.
        qs.Should().HaveCount(2);
        qs.Should().AllSatisfy(q => q.Id.Should().Contain("movie"));
    }
}
