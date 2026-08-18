using BenchmarkDotNet.Running;

namespace Scrinia.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        // BenchmarkSwitcher discovers every class in the assembly that has at least one
        // [Benchmark]-attributed method — registration is automatic, not a list maintained here.
        // Run with: dotnet run -c Release --project tests/Scrinia.Benchmarks
        //   [--filter *Bm25*]                       run a subset
        //   [--filter *WritePathBenchmarks*]        store write path (task 7, part D.1)
        //   [--filter *VectorWriteBenchmarks*]      vector write path (task 7, part D.2)
        //   [--exporters json]                      machine-readable summary
        //   [--artifacts ./benchmark-results]       output directory
        // scripts/run-write-path-benchmarks.ps1 wraps the two write-path filters above with the
        // -c Release build step and a results path under artifacts/benchmarks/.
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
