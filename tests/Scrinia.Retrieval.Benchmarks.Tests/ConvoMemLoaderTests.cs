using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Loaders;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Verifies the ConvoMem JSON loader parses the published schema correctly: one
/// question per evidence item, haystack docs built from all conversation messages,
/// ground-truth texts pulled verbatim from <c>message_evidences</c>, category named
/// after the source filename. Real ConvoMem files follow this exact shape — the
/// fixture is a minimal valid instance.
/// </summary>
public class ConvoMemLoaderTests : IAsyncLifetime
{
    private string _root = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "scrinia-convomem-loader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Parses_evidence_items_into_questions_with_haystack_and_ground_truth()
    {
        const string json = """
        {
          "evidence_items": [
            {
              "question": "What does the user prefer in phones?",
              "answer": "Long battery life",
              "conversations": [
                {
                  "messages": [
                    {"speaker": "user", "text": "I hate phones with bad battery"},
                    {"speaker": "assistant", "text": "I can recommend long-battery options"}
                  ]
                }
              ],
              "message_evidences": [
                {"speaker": "user", "text": "I hate phones with bad battery"}
              ]
            },
            {
              "question": "What sport does the user enjoy?",
              "answer": "Tennis",
              "conversations": [
                {
                  "messages": [
                    {"speaker": "user", "text": "I love playing tennis on weekends"}
                  ]
                }
              ],
              "message_evidences": [
                {"speaker": "user", "text": "I love playing tennis"}
              ]
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "user_evidence.json"), json);

        var loader = new ConvoMemLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(2);
        qs[0].Id.Should().Be("user_evidence_0");
        qs[0].Category.Should().Be("user_evidence");
        qs[0].QueryText.Should().Be("What does the user prefer in phones?");
        qs[0].Haystack.Should().HaveCount(2);
        qs[0].Haystack[0].Text.Should().Contain("bad battery");
        qs[0].GroundTruthTexts.Should().ContainSingle().Which.Should().Contain("bad battery");
        qs[0].GroundTruthIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Per_category_limit_caps_questions_per_file()
    {
        const string json = """
        {
          "evidence_items": [
            {"question": "Q1", "conversations": [{"messages": [{"speaker": "user", "text": "answer1"}]}], "message_evidences": [{"text": "answer1"}]},
            {"question": "Q2", "conversations": [{"messages": [{"speaker": "user", "text": "answer2"}]}], "message_evidences": [{"text": "answer2"}]},
            {"question": "Q3", "conversations": [{"messages": [{"speaker": "user", "text": "answer3"}]}], "message_evidences": [{"text": "answer3"}]}
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "preference_evidence.json"), json);

        var loader = new ConvoMemLoader(_root, perCategoryLimit: 2);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Items_missing_evidence_or_messages_are_skipped()
    {
        const string json = """
        {
          "evidence_items": [
            {"question": "ok", "conversations": [{"messages": [{"speaker": "user", "text": "hi"}]}], "message_evidences": [{"text": "hi"}]},
            {"question": "no-haystack", "conversations": [], "message_evidences": [{"text": "x"}]},
            {"question": "no-evidence", "conversations": [{"messages": [{"speaker": "user", "text": "hi"}]}], "message_evidences": []}
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_root, "abstention_evidence.json"), json);

        var loader = new ConvoMemLoader(_root);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(1);
        qs[0].QueryText.Should().Be("ok");
    }
}
