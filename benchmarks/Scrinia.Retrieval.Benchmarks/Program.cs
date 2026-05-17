using System.Globalization;
using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Cli;

namespace Scrinia.Retrieval.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        string subcommand = args[0];
        string[] rest = args[1..];
        try
        {
            return subcommand switch
            {
                "convomem" => await ConvoMemCommand.RunAsync(rest, CancellationToken.None),
                _ => UnknownCommand(subcommand),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string sub)
    {
        Console.Error.WriteLine($"unknown subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            scrinia-bench — Scrinia retrieval-quality benchmark harness

            Usage:
              scrinia-bench convomem --data <dir> [--limit <N>] [--top-k <K>] [--output <file>]

            Subcommands:
              convomem      Run ConvoMem (Salesforce) evidence questions

            Common flags:
              --data <dir>     Path to the dataset (downloaded manually; see benchmarks README)
              --limit <N>      Cap questions per category (ConvoMem default: 50/category)
              --top-k <K>      Retrieve top-K hits per query (default: 50)
              --output <file>  Write JSON result to this path (default: results/<bench>_<mode>_<UTC>.json)
            """);
    }

    /// <summary>
    /// Tiny argparse — looks up `--flag value` pairs and returns the value or null.
    /// Boolean flags are not supported (every flag must take a value).
    /// </summary>
    public static string? Flag(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    public static int FlagInt(string[] args, string name, int @default)
    {
        string? raw = Flag(args, name);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInvariant, out int parsed) ? parsed : @default;
    }

    public static readonly CultureInfo CultureInvariant = CultureInfo.InvariantCulture;

    public static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
