namespace Scrinia.Agent.Benchmarks;

public sealed record RunConfiguration(int SchemaVersion, string Dataset, string[] CaseIds,
    ScriniaSettings Scrinia, ModelSettings Agent, ModelSettings Judge, RunLimits Limits, string OutputDirectory);
