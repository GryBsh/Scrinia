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
    public async Task Subdirectory_layout_uses_evidence_type_as_category_with_per_category_limit()
    {
        // Published ConvoMem layout: evidence_questions/<evidence_type>/<N>_evidence/<persona>.json.
        // Category should be the evidence_type subdir name (not the persona filename), and the
        // per-category limit caps across all persona files within that evidence type.
        const string template = """
        {"evidence_items": [
          {"question": "q1", "conversations": [{"messages": [{"speaker": "u", "text": "ans1"}]}], "message_evidences": [{"text": "ans1"}]},
          {"question": "q2", "conversations": [{"messages": [{"speaker": "u", "text": "ans2"}]}], "message_evidences": [{"text": "ans2"}]},
          {"question": "q3", "conversations": [{"messages": [{"speaker": "u", "text": "ans3"}]}], "message_evidences": [{"text": "ans3"}]}
        ]}
        """;
        // Two persona files under the same evidence-type subdir.
        var dir = Directory.CreateDirectory(Path.Combine(_root, "abstention_evidence", "1_evidence"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "persona_a.json"), template);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "persona_b.json"), template);

        // Per-category limit of 4 across the 6 total items (3 per persona × 2 personas).
        var loader = new ConvoMemLoader(_root, perCategoryLimit: 4);
        var qs = new List<Core.BenchmarkQuestion>();
        await foreach (var q in loader.LoadAsync()) qs.Add(q);

        qs.Should().HaveCount(4, because: "the limit caps across personas within the evidence-type bucket");
        qs.Should().AllSatisfy(q => q.Category.Should().Be("abstention_evidence"));
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
