using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class DerivedClassifierEquivalenceTests
{
    [Theory]
    [InlineData("embeddings/model.bin", "EmbeddingIndexes")]
    [InlineData("indexes/search.db", "SearchIndexes")]
    [InlineData("exports/bundle.nmp", "ExportBundles")]
    [InlineData("versions/old.nmp", "ArchivedCopies")]
    [InlineData("facts.json", "EntityFacts")]
    [InlineData(".restore-journal.json", "RestoreJournal")]
    [InlineData("topic/cache.tmp", "TemporaryTree")]
    [InlineData("topic/cache.tmp/child.bin", "TemporaryTree")]
    [InlineData("worker.lock", "RootLockTree")]
    [InlineData("worker.lock/child.bin", "RootLockTree")]
    public void OrderedClassifierUsesTheSameDerivedSetForEveryConsumer(string path, string expectedClass)
    {
        DerivedPathClassification classification = SharedClassifier.Classify(path);

        classification.Class.Should().Be(Enum.Parse<DerivedPathClass>(expectedClass));
        SharedClassifier.IsExcludedFromFingerprint(path).Should().BeTrue();
        SharedClassifier.CleanupPathspecs(classification).Should().NotBeEmpty();
        SharedClassifier.IgnorePatterns(classification).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("topics/embeddings/authoritative.nmp2")]
    [InlineData("facts.json.meta.json")]
    [InlineData("topic/ordinary.nmp2")]
    public void AuthoritativePathsAreNotDerived(string path)
    {
        SharedClassifier.Classify(path).Class.Should().Be(DerivedPathClass.Authoritative);
        SharedClassifier.IsExcludedFromFingerprint(path).Should().BeFalse();
    }

    [Fact]
    public void RulesAreOrderedAndConsumerOutputsComeFromThoseRules()
    {
        SharedClassifier.Rules.Select(rule => rule.Class)
            .Should().Equal(
                DerivedPathClass.EmbeddingIndexes,
                DerivedPathClass.SearchIndexes,
                DerivedPathClass.ExportBundles,
                DerivedPathClass.ArchivedCopies,
                DerivedPathClass.EntityFacts,
                DerivedPathClass.RestoreJournal,
                DerivedPathClass.TemporaryTree,
                DerivedPathClass.RootLockTree);
    }
}
