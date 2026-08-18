using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Scrinia.Benchmarks;

/// <summary>
/// <see cref="RunStrategy.Monitoring"/> (one invocation per iteration — required by any benchmark
/// whose <c>IterationSetup</c>/<c>IterationCleanup</c> mutate real on-disk state, see
/// <see cref="WritePathBenchmarks"/> and <see cref="VectorWriteBenchmarks"/>) combined with the
/// in-process toolchain.
///
/// <para>The in-process toolchain is not a preference — it is a workaround. BenchmarkDotNet's
/// default out-of-process toolchain generates a throwaway csproj and resolves it by searching the
/// whole repository tree for a project file matching the benchmark assembly's name. This
/// workspace carries an unrelated stray git worktree under <c>.claude/worktrees/</c> that happens
/// to contain its own checked-out copy of <c>tests/Scrinia.Benchmarks/Scrinia.Benchmarks.csproj</c>,
/// so that search finds two matches and BenchmarkDotNet throws
/// <c>NotSupportedException: Found more than one matching project file</c> before a single
/// iteration runs. In-process execution skips project generation entirely, so the collision never
/// enters the picture. <see cref="LoadIndexBenchmarks"/> and <see cref="Bm25ScorerBenchmarks"/>
/// predate that stray worktree's presence and were not touched here.</para>
///
/// <para>Combining two independent job characteristics (strategy + toolchain) can't be done with
/// two separate attributes on one class — <c>[SimpleJob(...)]</c> plus <c>[InProcess]</c> would
/// register two distinct jobs and run the benchmark twice, once per job, not once under the union
/// of both settings. A single <see cref="Job"/> built here and added once is the union.</para>
/// </summary>
internal sealed class MonitoringInProcessConfig : ManualConfig
{
    public MonitoringInProcessConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithToolchain(InProcessEmitToolchain.Instance));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
