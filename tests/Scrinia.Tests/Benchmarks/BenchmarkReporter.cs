using Xunit.Abstractions;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Formatted table and summary output for benchmark results.
/// Follows the same pattern as <see cref="Nmp2BenchmarkTests"/>.
/// </summary>
public static class BenchmarkReporter
{
    /// <summary>
    /// Writes a comparison table with auto-width columns.
    /// </summary>
    public static void WriteComparisonTable(
        ITestOutputHelper output,
        string title,
        string[] headers,
        IReadOnlyList<string[]> rows)
    {
        output.WriteLine("");
        output.WriteLine($"=== {title} ===");
        output.WriteLine("");

        // Compute column widths
        int[] widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
            widths[c] = headers[c].Length;

        foreach (var row in rows)
            for (int c = 0; c < row.Length && c < widths.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);

        // Header
        var header = string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i])));
        output.WriteLine(header);
        output.WriteLine(new string('-', header.Length));

        // Rows
        foreach (var row in rows)
        {
            var line = string.Join("  ", row.Select((v, i) => i < widths.Length ? v.PadRight(widths[i]) : v));
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// Writes a scaling table showing a metric across multiple scale points per system.
    /// </summary>
    public static void WriteScalingTable(
        ITestOutputHelper output,
        string title,
        string metric,
        int[] scales,
        Dictionary<string, double[]> systemValues)
    {
        output.WriteLine("");
        output.WriteLine($"=== {title} ===");
        output.WriteLine("");

        // Headers: System | scale1 | scale2 | ...
        var headers = new string[scales.Length + 1];
        headers[0] = "System";
        for (int i = 0; i < scales.Length; i++)
            headers[i + 1] = $"{metric}@{scales[i]}";

        int[] widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
            widths[c] = headers[c].Length;

        var rows = new List<string[]>();
        foreach (var (system, values) in systemValues)
        {
            var row = new string[headers.Length];
            row[0] = system;
            widths[0] = Math.Max(widths[0], system.Length);
            for (int i = 0; i < values.Length && i < scales.Length; i++)
            {
                row[i + 1] = values[i].ToString("N1");
                widths[i + 1] = Math.Max(widths[i + 1], row[i + 1].Length);
            }
            rows.Add(row);
        }

        var header = string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i])));
        output.WriteLine(header);
        output.WriteLine(new string('-', header.Length));
        foreach (var row in rows)
        {
            var line = string.Join("  ", row.Select((v, i) => (v ?? "").PadRight(widths[i])));
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// Writes a summary verdict line.
    /// </summary>
    public static void WriteVerdict(
        ITestOutputHelper output,
        string dimension,
        string winner,
        string reason)
    {
        output.WriteLine($"  >> {dimension}: {winner} — {reason}");
    }
}
