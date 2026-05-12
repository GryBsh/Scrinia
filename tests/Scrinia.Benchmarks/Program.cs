using BenchmarkDotNet.Running;

namespace Scrinia.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        // BenchmarkSwitcher discovers every [MemoryDiagnoser]-tagged class in the assembly.
        // Run with: dotnet run -c Release --project tests/Scrinia.Benchmarks
        //   [--filter *Bm25*]                       run a subset
        //   [--exporters json]                      machine-readable summary
        //   [--artifacts ./benchmark-results]       output directory
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
