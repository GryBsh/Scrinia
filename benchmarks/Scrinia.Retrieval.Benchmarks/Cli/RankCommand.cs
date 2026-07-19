using System.Text.Json;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// One row of the <c>rank</c> table: a single result file's judge metrics plus the
/// <see cref="Core.RunMetadata"/> context needed to compare arms. <see cref="CorrectRate"/>
/// is either the file's <c>Aggregate.CorrectRate</c> (Task 1 schema), or — for result files
/// written before that schema split — the legacy <c>Aggregate.Answerable</c> field.
/// </summary>
public sealed record RankRow(
    string File,
    int QuestionCount,
    double CorrectRate,
    double SurfacedRate,
    string? Granularity,
    string? TemporalScenario);

/// <summary>
/// Ranks prior benchmark result files by the LLM-judge <c>CorrectRate</c>/<c>SurfacedRate</c>
/// metric (Task 1's <see cref="Core.AggregateMetrics"/>). Tolerant of pre-schema-split result
/// files, which carry a legacy <c>Aggregate.Answerable</c> field instead — that value is used
/// as the <see cref="RankRow.CorrectRate"/> fallback.
///
/// <para>Result files under <c>results/</c> run 25-180 MB each, almost entirely the
/// <c>Questions</c> array — <see cref="RunMetadata"/> and <see cref="Core.AggregateMetrics"/>
/// are the first two top-level keys, always ahead of <c>Questions</c> in emission order (see
/// <see cref="Core.BenchmarkRunResult"/>). So rather than <c>JsonDocument.Parse</c> the whole
/// file, <see cref="Summarize"/> streams a growing buffer through a multi-segment <see
/// cref="Utf8JsonReader"/> and stops the instant it sees the <c>Questions</c> property name —
/// the array itself is never read off disk, which is what keeps scanning ~70 such files fast
/// and allocation-light instead of risking OOM.</para>
/// </summary>
internal static class RankCommand
{
    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string dir = Program.Flag(args, "--dir")
            ?? Path.Combine("benchmarks", "Scrinia.Retrieval.Benchmarks", "results");

        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"[scrinia-bench:rank] results dir not found: {dir}");
            return Task.FromResult(1);
        }

        ct.ThrowIfCancellationRequested();

        var files = Directory.EnumerateFiles(dir, "scrinia_locomo*.json")
            .Concat(Directory.EnumerateFiles(dir, "*.judged.json"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"[scrinia-bench:rank] no result files matching scrinia_locomo*.json / *.judged.json under {dir}");
            return Task.FromResult(0);
        }

        var rows = Summarize(files);
        PrintTable(rows);
        return Task.FromResult(0);
    }

    /// <summary>
    /// Reads the <c>RunMetadata</c>+<c>Aggregate</c> prefix of every file (skipping, with a
    /// warning, any file that can't be opened or parsed) and returns one <see cref="RankRow"/>
    /// per readable file, sorted by <see cref="RankRow.CorrectRate"/> descending.
    /// </summary>
    public static IReadOnlyList<RankRow> Summarize(IEnumerable<string> files)
    {
        var rows = new List<RankRow>();
        foreach (string file in files)
        {
            RankRow? row;
            try
            {
                row = SummarizeOne(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"[scrinia-bench:rank] skipping '{file}': {ex.GetType().Name}: {ex.Message}");
                row = null;
            }

            if (row is not null)
                rows.Add(row);
        }

        return rows.OrderByDescending(r => r.CorrectRate).ToList();
    }

    private static RankRow SummarizeOne(string file)
    {
        using var stream = File.OpenRead(file);
        var header = ReadHeader(stream);
        return new RankRow(
            file,
            header.QuestionCount,
            header.CorrectRate,
            header.SurfacedRate,
            header.Granularity,
            header.TemporalScenario);
    }

    private readonly record struct Header(
        int QuestionCount,
        double CorrectRate,
        double SurfacedRate,
        string? Granularity,
        string? TemporalScenario);

    private const int InitialBufferBytes = 16 * 1024;
    private const int MaxBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Streams only the <c>RunMetadata</c>+<c>Aggregate</c> prefix of a result file with a
    /// multi-segment <see cref="Utf8JsonReader"/> over a growing buffer (growth only happens
    /// if a single token doesn't fit; header fields are always short, so in practice this
    /// never grows past the initial buffer). Stops the moment the <c>Questions</c> property
    /// name is seen at the root object's depth — the array that follows, which is what makes
    /// these files 25-180 MB, is never read off disk.
    /// </summary>
    private static Header ReadHeader(FileStream stream)
    {
        byte[] buffer = new byte[InitialBufferBytes];
        int leftOver = 0;
        var state = new JsonReaderState();

        string? currentSection = null; // "RunMetadata" | "Aggregate" | null (uninteresting section)
        string? pendingProperty = null; // depth-2 field name awaiting its scalar value
        const int FieldDepth = 2;

        int questionCount = 0;
        double correctRate = 0.0;
        bool sawCorrectRate = false;
        double surfacedRate = 0.0;
        double? legacyAnswerable = null;
        string? granularity = null;
        string? temporalScenario = null;

        while (true)
        {
            int bytesRead = stream.Read(buffer, leftOver, buffer.Length - leftOver);
            int dataLen = leftOver + bytesRead;
            bool isFinalBlock = bytesRead == 0;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLen), isFinalBlock, state);

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.CurrentDepth == 1)
                    {
                        string name = reader.GetString() ?? string.Empty;
                        if (name == "Questions")
                            return Finish();

                        currentSection = name is "RunMetadata" or "Aggregate" ? name : null;
                        pendingProperty = null;
                    }
                    else if (reader.CurrentDepth == FieldDepth && currentSection is not null)
                    {
                        pendingProperty = reader.GetString();
                    }

                    continue;
                }

                if (pendingProperty is null || currentSection is null || reader.CurrentDepth != FieldDepth)
                    continue;

                switch (currentSection, pendingProperty)
                {
                    case ("RunMetadata", "Granularity") when reader.TokenType == JsonTokenType.String:
                        granularity = reader.GetString();
                        break;
                    case ("RunMetadata", "TemporalScenario") when reader.TokenType == JsonTokenType.String:
                        temporalScenario = reader.GetString();
                        break;
                    case ("Aggregate", "QuestionCount") when reader.TokenType == JsonTokenType.Number:
                        questionCount = reader.GetInt32();
                        break;
                    case ("Aggregate", "CorrectRate") when reader.TokenType == JsonTokenType.Number:
                        correctRate = reader.GetDouble();
                        sawCorrectRate = true;
                        break;
                    case ("Aggregate", "SurfacedRate") when reader.TokenType == JsonTokenType.Number:
                        surfacedRate = reader.GetDouble();
                        break;
                    case ("Aggregate", "Answerable") when reader.TokenType == JsonTokenType.Number:
                        legacyAnswerable = reader.GetDouble();
                        break;
                }

                pendingProperty = null;
            }

            state = reader.CurrentState;
            long consumed = reader.BytesConsumed;
            if (isFinalBlock)
                return Finish();

            int remaining = dataLen - (int)consumed;
            if (remaining == dataLen)
            {
                // A single token didn't fit in the buffer at all. Header fields are always
                // short in practice, so this is a defensive path only — give up gracefully
                // (with whatever's been gathered so far) rather than growing without bound.
                if (buffer.Length >= MaxBufferBytes)
                    return Finish();

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxBufferBytes));
            }
            else if (remaining > 0)
            {
                Buffer.BlockCopy(buffer, (int)consumed, buffer, 0, remaining);
            }

            leftOver = remaining;
        }

        Header Finish() => new(
            questionCount,
            sawCorrectRate ? correctRate : legacyAnswerable ?? 0.0,
            surfacedRate,
            granularity,
            temporalScenario);
    }

    private static void PrintTable(IReadOnlyList<RankRow> rows)
    {
        string[] headers = ["File", "N", "Correct%", "Surfaced%", "Granularity", "Scenario"];
        var data = rows.Select(r => new[]
        {
            Path.GetFileName(r.File),
            r.QuestionCount.ToString(Program.CultureInvariant),
            (r.CorrectRate * 100).ToString("F1", Program.CultureInvariant),
            (r.SurfacedRate * 100).ToString("F1", Program.CultureInvariant),
            r.Granularity ?? "-",
            r.TemporalScenario ?? "-",
        }).ToList();

        int[] widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in data)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        Console.WriteLine(string.Join("  ", headers.Select((h, c) => h.PadRight(widths[c]))));
        foreach (var row in data)
            Console.WriteLine(string.Join("  ", row.Select((v, c) => v.PadRight(widths[c]))));
    }
}
