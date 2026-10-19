using System.Text;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed record PhaseStatistics(int ModelRequests, int ToolCalls, int ToolErrors, double DurationMs, UsageTotals Usage);
public sealed record AttemptStatistics(string EvidencePath, bool PartialEvidence, Dictionary<string, PhaseStatistics> Phases);
public sealed record RunReport(AccuracySummary Overall, Dictionary<string, AccuracySummary> Categories,
    Dictionary<string, int> States, UsageTotals CaseUsage, UsageTotals PreflightUsage,
    AttemptStatistics[] Attempts, AttemptStatistics[] Preflight, string ExportPolicy);

public static class SummaryWriter
{
    public static async Task WriteAsync(string root, DatasetSnapshot dataset, RunJournal journal, TextWriter output, CancellationToken ct)
    {
        var outcomes = await journal.ReadOutcomesAsync(ct);
        var overall = RunSummary.Compute(journal.Manifest.SelectedIds, outcomes);
        var categories = dataset.Cases.GroupBy(c => c.Evaluation.Category).ToDictionary(g => g.Key,
            g => RunSummary.Compute(g.Select(c => c.Evaluation.QuestionId).ToArray(), outcomes.Where(o => g.Any(c => c.Evaluation.QuestionId == o.QuestionId)).ToArray()), StringComparer.Ordinal);
        var states = Enum.GetValues<CaseState>().ToDictionary(s => s.ToString(), s => outcomes.Count(o => o.State == s), StringComparer.Ordinal);
        states[nameof(CaseState.Pending)] += overall.Selected - outcomes.Count;
        var attempts = ReadStatistics(Path.Combine(root, "cases"), root, preflight: false);
        var preflight = ReadStatistics(Path.Combine(root, "preflight"), root, preflight: true);
        var report = new RunReport(overall, categories, states, Total(attempts), Total(preflight), attempts, preflight,
            "answers.jsonl contains one verified answer from the latest attempt per question, including incorrect and ungraded answers. Prior answers remain in immutable attempt evidence.");
        var answers = new StringBuilder();
        foreach (var outcome in outcomes.Where(o => o.AnswerSha256 is not null))
        {
            int ordinal = Array.IndexOf(journal.Manifest.SelectedIds, outcome.QuestionId) + 1;
            var answer = journal.ReadAnswer(journal.PathsFor(ordinal, outcome.Attempt), outcome.AnswerSha256);
            answers.AppendLine(ChatEvidence.Build(w => { w.WriteString("question_id", outcome.QuestionId); w.WriteString("hypothesis", answer.Text); }).GetRawText());
        }
        await CaseRunner.Persist(() => new AtomicJsonFile().WriteAsync(Path.Combine(root, "answers.jsonl"), Encoding.UTF8.GetBytes(answers.ToString()), ct));
        await CaseRunner.Persist(() => new AtomicJsonFile().WriteAsync(Path.Combine(root, "summary.json"), JsonSerializer.SerializeToUtf8Bytes(report, RecordJsonContext.Default.RunReport), ct));
        await output.WriteLineAsync($"Accuracy: {overall.Correct}/{overall.Graded}; coverage: {overall.Graded}/{overall.Selected}; end-to-end success: {overall.Correct}/{overall.Selected}{(overall.Provisional ? " (provisional)" : "")}");
        await output.WriteLineAsync($"Case tokens: {Display(report.CaseUsage)}; preflight tokens: {Display(report.PreflightUsage)}. See summary.json for all attempts and phase costs.");
    }

    private static string Display(UsageTotals usage) => $"input={usage.InputTokens?.ToString() ?? "unknown"}, output={usage.OutputTokens?.ToString() ?? "unknown"}";
    private static UsageTotals Total(IEnumerable<AttemptStatistics> attempts)
    {
        long? input = 0, output = 0;
        foreach (var attempt in attempts)
        {
            if (attempt.PartialEvidence) input = output = null;
            foreach (var phase in attempt.Phases.Values)
            {
                input = input is not null && phase.Usage.InputTokens is not null ? checked(input + phase.Usage.InputTokens) : null;
                output = output is not null && phase.Usage.OutputTokens is not null ? checked(output + phase.Usage.OutputTokens) : null;
            }
        }
        return new(input, output);
    }

    private static AttemptStatistics[] ReadStatistics(string parent, string root, bool preflight)
    {
        if (!Directory.Exists(parent)) return [];
        // Only harness evidence paths are visited, never model-created workspace files.
        var directories = preflight ? Directory.EnumerateDirectories(parent) : Directory.EnumerateDirectories(parent)
            .SelectMany(c => Directory.Exists(Path.Combine(c, "attempts")) ? Directory.EnumerateDirectories(Path.Combine(c, "attempts")) : [])
            .Select(a => Path.Combine(a, "evidence"));
        var result = new List<AttemptStatistics>();
        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, "events.ndjson");
            PathSafety.RequireNoLinks(path);
            var phases = new Dictionary<string, PhaseAccumulator>(StringComparer.Ordinal);
            bool partial = false;
            if (!File.Exists(path)) partial = true;
            else
            {
                var bytes = File.ReadAllBytes(path);
                int start = 0; long sequence = 1;
                while (start < bytes.Length)
                {
                    int end = Array.IndexOf(bytes, (byte)'\n', start);
                    if (end < 0) { partial = true; break; }
                    RunEvent item;
                    try { item = JsonSerializer.Deserialize(bytes.AsSpan(start, end - start), RecordJsonContext.Default.RunEvent) ?? throw new InvalidDataException("Null event."); }
                    catch (JsonException error) { throw new InvalidDataException("Invalid event in summary evidence.", error); }
                    if (item.Sequence != sequence++) throw new InvalidDataException("Invalid event order in summary evidence.");
                    if (!phases.TryGetValue(item.Phase, out var phase)) phases.Add(item.Phase, phase = new());
                    phase.Add(item);
                    start = end + 1;
                }
            }
            result.Add(new(Path.GetRelativePath(root, path).Replace('\\', '/'), partial, phases.ToDictionary(p => p.Key, p => p.Value.Finish(), StringComparer.Ordinal)));
        }
        return result.ToArray();
    }

    private sealed class PhaseAccumulator
    {
        private int _requests, _responses, _calls, _errors;
        private double _duration;
        private long? _input = 0, _output = 0;
        public void Add(RunEvent item)
        {
            var data = item.Data;
            switch (item.Kind)
            {
                case "model-request": _requests++; break;
                case "model-response":
                    _responses++;
                    var usage = data.GetProperty("usage").Deserialize(RecordJsonContext.Default.UsageTotals)!;
                    _input = _input is not null && usage.InputTokens is >= 0 ? checked(_input + usage.InputTokens) : null;
                    _output = _output is not null && usage.OutputTokens is >= 0 ? checked(_output + usage.OutputTokens) : null;
                    break;
                case "model-error": _input = _output = null; break;
                case "tool-intent": _calls++; break;
                case "tool-error": _errors++; break;
                case "tool-result": if (data.GetProperty("error").GetBoolean()) _errors++; break;
                case "phase-end": _duration += data.GetProperty("durationMs").GetDouble(); break;
            }
        }
        public PhaseStatistics Finish() => new(_requests, _calls, _errors, _duration,
            _requests == _responses ? new(_input, _output) : new(null, null));
    }
}
