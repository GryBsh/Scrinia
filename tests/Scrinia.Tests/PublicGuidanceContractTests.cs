using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class PublicGuidanceContractTests
{
    [Fact]
    public void Living_guidance_publishes_strict_migration_contract()
    {
        string root = RepoPaths.Root();
        string guidance = string.Join('\n',
            "docs/getting-started.md",
            "docs/cli-reference.md",
            "docs/plugin-authoring.md",
            "docs/troubleshooting.md",
            "CHANGELOG.md")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => File.ReadAllText(Path.Combine(root, path)))
            .Aggregate((left, right) => left + "\n" + right);

        Assert.Contains("MALFORMED_ADDRESS", guidance, StringComparison.Ordinal);
        Assert.Contains("BUNDLE_RECOVERY_REQUIRED", guidance, StringComparison.Ordinal);
        Assert.Contains("scrinia.embeddings.protocol = 1", guidance, StringComparison.Ordinal);
        Assert.Contains("Scrinia:Search:CandidateLimit", guidance, StringComparison.Ordinal);
        Assert.Contains("quarantine", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reindex", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partial import succeeded", guidance, StringComparison.OrdinalIgnoreCase);
    }
}
