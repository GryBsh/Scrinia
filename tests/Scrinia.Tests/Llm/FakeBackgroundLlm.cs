using Scrinia.Core.Llm;

namespace Scrinia.Tests.Llm;

/// <summary>
/// In-memory <see cref="IBackgroundLlm"/> for tests. Returns canned strings, counts calls,
/// and lets tests stage per-method null responses to exercise the failure paths
/// the consolidator handles (timeouts, garbage output, etc.).
/// </summary>
internal sealed class FakeBackgroundLlm : IBackgroundLlm
{
    public bool Available { get; set; } = true;
    public string? DescriptionResponse { get; set; } = "Auto-generated description.";
    public string? SummaryResponse { get; set; } = "Auto-generated summary paragraph for a session log.";
    public string[]? FactsResponse { get; set; } = ["Fact one is true.", "Fact two adds detail.", "Fact three concludes."];
    public int? ImportanceResponse { get; set; } = 6;

    public int DescriptionCalls { get; private set; }
    public int SummaryCalls { get; private set; }
    public int FactsCalls { get; private set; }
    public int ImportanceCalls { get; private set; }
    public int AvailabilityCalls { get; private set; }

    /// <summary>The content argument passed to the most recent ScoreImportanceAsync call.</summary>
    public string? LastImportanceContent { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        AvailabilityCalls++;
        return Task.FromResult(Available);
    }

    public Task<string?> GenerateDescriptionAsync(string content, CancellationToken ct)
    {
        DescriptionCalls++;
        return Task.FromResult(DescriptionResponse);
    }

    public Task<string?> SummarizeAsync(string text, CancellationToken ct)
    {
        SummaryCalls++;
        return Task.FromResult(SummaryResponse);
    }

    public Task<string[]?> ExtractFactsAsync(string content, CancellationToken ct)
    {
        FactsCalls++;
        return Task.FromResult(FactsResponse);
    }

    public Task<int?> ScoreImportanceAsync(string content, CancellationToken ct)
    {
        ImportanceCalls++;
        LastImportanceContent = content;
        return Task.FromResult(ImportanceResponse);
    }
}
