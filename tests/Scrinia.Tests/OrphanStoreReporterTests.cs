using System.IO;
using Scrinia.Commands;

namespace Scrinia.Tests;

public class OrphanStoreReporterTests
{
    [Fact]
    public void ListTopics_ExtractsLegacyNamespacedTopics()
    {
        string dir = Path.Combine(Path.GetTempPath(), "orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "topics", "entity", "memory"));
        Directory.CreateDirectory(Path.Combine(dir, "topics", "memory", "finding"));
        Directory.CreateDirectory(Path.Combine(dir, "topics", "agent"));

        var topics = OrphanStoreReporter.ListTopics(dir);

        Assert.Contains("finding", topics);
        Assert.Contains("memory", topics);
        Assert.Contains("agent", topics);
        // The namespace directories themselves are not topics.
        Assert.DoesNotContain("entity", topics);
    }

    [Fact]
    public void FormatOrphanWarning_WithTopics_IncludesMigrationCommand()
    {
        var topics = new[] { "memory", "finding" };
        string result = OrphanStoreReporter.FormatOrphanWarning("/orphan/root", "/workspace/root", topics);

        Assert.Contains("scri bundle export", result);
        Assert.Contains("/workspace/root", result);
    }
}
