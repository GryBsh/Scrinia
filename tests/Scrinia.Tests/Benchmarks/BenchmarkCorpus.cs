namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Deterministic generator for benchmark facts. Round-robins across 5 topics
/// with indexed substitution arrays to produce varied but reproducible content.
/// </summary>
public static class BenchmarkCorpus
{
    public static readonly string[] Topics = ["api", "arch", "config", "deploy", "debug"];

    // ── Substitution arrays (8 entries each) ──────────────────────────

    private static readonly string[] ApiEndpoints =
        ["users", "orders", "products", "sessions", "webhooks", "billing", "teams", "notifications"];

    private static readonly string[] AuthMethods =
        ["JWT bearer", "OAuth2 PKCE", "API key header", "mTLS", "SAML assertion", "session cookie", "HMAC signature", "OpenID Connect"];

    private static readonly string[] ArchComponents =
        ["gateway", "scheduler", "cache layer", "message broker", "service mesh", "load balancer", "circuit breaker", "rate limiter"];

    private static readonly string[] ArchPatterns =
        ["event sourcing", "CQRS", "saga orchestration", "outbox pattern", "bulkhead isolation", "retry with backoff", "fan-out/fan-in", "competing consumers"];

    private static readonly string[] ConfigSettings =
        ["connection timeout", "retry count", "batch size", "log level", "thread pool size", "cache TTL", "health check interval", "max connections"];

    private static readonly string[] ConfigFormats =
        ["YAML", "TOML", "JSON", "environment variables", "INI files", "XML", "HCL", "dotenv"];

    private static readonly string[] DeployTargets =
        ["Kubernetes", "Docker Swarm", "AWS ECS", "Azure Container Apps", "bare metal", "Fly.io", "Railway", "Nomad"];

    private static readonly string[] DeployStrategies =
        ["blue-green", "canary", "rolling update", "recreate", "A/B testing", "shadow traffic", "feature flags", "traffic splitting"];

    private static readonly string[] DebugTools =
        ["distributed tracing", "structured logging", "profiler", "memory dump", "network capture", "flame graph", "core dump", "debugger attach"];

    private static readonly string[] DebugSymptoms =
        ["memory leak", "deadlock", "race condition", "stack overflow", "null reference", "timeout cascade", "connection pool exhaustion", "thread starvation"];

    /// <summary>
    /// Generates <paramref name="count"/> deterministic facts, round-robin across 5 topics.
    /// Each fact contains a unique needle term for precision testing.
    /// </summary>
    public static IReadOnlyList<BenchmarkFact> Generate(int count)
    {
        var facts = new List<BenchmarkFact>(count);
        for (int i = 0; i < count; i++)
        {
            string topic = Topics[i % Topics.Length];
            string key = $"{topic}-{i:D4}";
            string needle = $"zyx{topic}{i:D4}";

            string content = topic switch
            {
                "api" => $"The {Pick(ApiEndpoints, i)} API endpoint requires {Pick(AuthMethods, i)} authentication. " +
                          $"Rate limiting is set to {100 + i * 10} requests per minute. " +
                          $"Internal reference: {needle}. " +
                          $"Response payloads use envelope format with pagination cursors.",

                "arch" => $"The {Pick(ArchComponents, i)} component implements the {Pick(ArchPatterns, i)} pattern. " +
                           $"It handles up to {1000 + i * 50} concurrent connections. " +
                           $"Internal reference: {needle}. " +
                           $"Horizontal scaling is achieved through consistent hashing.",

                "config" => $"The {Pick(ConfigSettings, i)} is configured via {Pick(ConfigFormats, i)} files. " +
                             $"Default value is {10 + i * 5} with a maximum of {100 + i * 20}. " +
                             $"Internal reference: {needle}. " +
                             $"Changes require a service restart to take effect.",

                "deploy" => $"Deployment to {Pick(DeployTargets, i)} uses a {Pick(DeployStrategies, i)} strategy. " +
                             $"Rollback window is {5 + i % 30} minutes with {2 + i % 5} health checks. " +
                             $"Internal reference: {needle}. " +
                             $"Artifact versioning follows semantic release conventions.",

                "debug" => $"Diagnosing {Pick(DebugSymptoms, i)} requires {Pick(DebugTools, i)} instrumentation. " +
                            $"Threshold alert fires at {80 + i % 15}% utilization. " +
                            $"Internal reference: {needle}. " +
                            $"Runbook is stored in the ops wiki under incident procedures.",

                _ => throw new InvalidOperationException($"Unknown topic: {topic}")
            };

            string question = topic switch
            {
                "api"    => $"{Pick(ApiEndpoints, i)} API authentication rate limiting",
                "arch"   => $"{Pick(ArchComponents, i)} {Pick(ArchPatterns, i)} scaling",
                "config" => $"{Pick(ConfigSettings, i)} {Pick(ConfigFormats, i)} configuration",
                "deploy" => $"{Pick(DeployTargets, i)} {Pick(DeployStrategies, i)} deployment",
                "debug"  => $"{Pick(DebugSymptoms, i)} {Pick(DebugTools, i)} diagnosis",
                _        => throw new InvalidOperationException()
            };

            facts.Add(new BenchmarkFact(topic, key, content, question, [needle]));
        }
        return facts;
    }

    /// <summary>
    /// Creates updated versions of facts at the specified indices.
    /// Updated facts have new content and retain the original content for comparison.
    /// </summary>
    public static IReadOnlyList<BenchmarkFact> GenerateUpdates(
        IReadOnlyList<BenchmarkFact> corpus, params int[] indices)
    {
        var updates = new List<BenchmarkFact>(indices.Length);
        foreach (int idx in indices)
        {
            var original = corpus[idx];
            string newNeedle = $"upd{original.Topic}{idx:D4}";
            string newContent = $"UPDATED: {original.Content.Replace(original.UniqueTerms[0], newNeedle)} " +
                                $"This fact was revised with new data. Reference: {newNeedle}.";

            updates.Add(original with
            {
                Content = newContent,
                UniqueTerms = [newNeedle],
                IsUpdate = true,
                OriginalContent = original.Content,
            });
        }
        return updates;
    }

    private static string Pick(string[] array, int index) => array[index % array.Length];
}
