namespace Scrinia.Agent.Benchmarks;

public sealed record ModelSettings(Uri Endpoint, string Model,
    string ApiKeyEnvironmentVariable, float Temperature, int MaxOutputTokens);
public sealed record RunLimits(int MaxModelTurnsPerEpisode, int EpisodeSeconds,
    int CaseSeconds, int MaxModelAttempts);
public sealed record ScriniaSettings(string PayloadDirectory, string Executable,
    string[] PrefixArguments, Dictionary<string, string> WorkspaceConfig, string? HomeSeedDirectory);
public sealed record RunConfiguration(int SchemaVersion, string Dataset, string[] CaseIds,
    ScriniaSettings Scrinia, ModelSettings Agent, ModelSettings Judge, RunLimits Limits, string OutputDirectory);
public sealed record AttemptPaths(string Root, string Workspace, string Home, string Evidence);
