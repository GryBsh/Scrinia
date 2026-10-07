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
            _ => Task.FromResult(1)
        };
}
