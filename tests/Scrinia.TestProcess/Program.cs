namespace Scrinia.TestProcess;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { return await DispatchAsync(args).ConfigureAwait(false); }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    internal static Task<int> DispatchAsync(string[] args) => args.Length == 0
        ? Task.FromResult(1)
        : args[0] switch
        {
            "admission" => AdmissionCommands.RunAsync(args[1..]),
            "vector" => VectorCommands.RunAsync(args[1..]),
            "tracking" => TrackingCommands.RunAsync(args[1..]),
            "publication-transport" => PublicationTransport.RunAsync(args[1..]),
            "shared-carrier" => SharedCarrierCommands.RunAsync(args[1..]),
            "pipe-holder-parent" => StartPipeHolderAsync(args[1..]),
            "pipe-holder-child" => HoldPipesAsync(args[1..]),
            _ => Task.FromResult(1)
        };

    private static async Task<int> StartPipeHolderAsync(string[] args)
    {
        string marker = PipeMarker(args);
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The current test-process host is unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("pipe-holder-child");
        start.ArgumentList.Add(marker);
        using var child = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("The owned pipe-holder child did not start.");
        bool ready = false;
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20).ConfigureAwait(false);
            ready = File.Exists(marker) && !child.HasExited;
            if (!ready) throw new TimeoutException("The owned pipe-holder child did not publish its identity.");
            return 0;
        }
        finally
        {
            if (!ready)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> HoldPipesAsync(string[] args)
    {
        string marker = PipeMarker(args);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        string started = ProcessBirthIdentity(self);
        string identity = self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
            started;
        File.WriteAllText(marker + ".pending", identity);
        File.Move(marker + ".pending", marker);
        Console.WriteLine("held stdout");
        Console.Error.WriteLine("held stderr");
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return 0;
    }

    private static string PipeMarker(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) ||
            !Directory.Exists(Path.GetDirectoryName(args[0])) || File.Exists(args[0]))
            throw new ArgumentException("A new absolute identity marker in the owned fixture directory is required.");
        return args[0];
    }

    private static string ProcessBirthIdentity(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsLinux())
            return process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string stat = File.ReadAllText($"/proc/{process.Id}/stat");
        int closing = stat.LastIndexOf(')');
        if (closing < 0) throw new InvalidDataException("The owned process stat has no command boundary.");
        string[] fields = stat[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("The owned process stat has no valid start-time identity.");
        return fields[19];
    }
}
