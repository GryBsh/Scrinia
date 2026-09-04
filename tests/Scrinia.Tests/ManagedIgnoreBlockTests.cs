using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class ManagedIgnoreBlockTests
{
    [Fact]
    public void LocalBlockIsExactLfAndNoBom()
    {
        ManagedIgnoreBlock.For(TrackingPolicy.Local)
            .Should().Be("# Scrinia tracking policy: local\n*\n");
    }

    [Fact]
    public void ProjectGitBlockIsExactAndAnchored()
    {
        ManagedIgnoreBlock.For(TrackingPolicy.ProjectGit)
            .Should().Be("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n");
    }

    [Fact]
    public void ApplyAppendsBlockAndPreservesUnrelatedLines()
    {
        string existing = "# user\r\nkeep\r\n";

        ManagedIgnoreBlock.Apply(existing, TrackingPolicy.ProjectGit)
            .Should().Be("# user\nkeep\n# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n");
    }

    [Fact]
    public void ValidateAcceptsAnExactManagedRegionWithUnrelatedContent()
    {
        string content = "# user\n" + ManagedIgnoreBlock.ProjectGit + "keep\n";

        ManagedIgnoreBlock.Validate(content, TrackingPolicy.ProjectGit)
            .Should().Be(ManagedIgnoreValidation.Valid);
    }

    [Fact]
    public void ApplyTransitionsFromLocalToProjectGitWithoutMovingUserLines()
    {
        string existing = "before\n" + ManagedIgnoreBlock.Local + "after\n";

        ManagedIgnoreBlock.Apply(existing, TrackingPolicy.ProjectGit)
            .Should().Be("before\n" + ManagedIgnoreBlock.ProjectGit + "after\n");
    }

    [Theory]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/indexes/\n# <<< Scrinia managed: project-git derived files <<<\n")]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/embeddings/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n")]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n")]
    public void DriftAndMarkerCollisionsAreRejected(string content)
    {
        ManagedIgnoreBlock.Validate(content, TrackingPolicy.ProjectGit)
            .Should().Be(ManagedIgnoreValidation.Drift);
    }

    [Theory]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n# >>> Scrinia managed: project-git derived files >>>\n")]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n# <<< Scrinia managed: project-git derived files <<<\n")]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n# >>> Scrinia managed: project-git derived files altered >>>\n")]
    [InlineData("# >>> Scrinia managed: project-git derived files >>>\n/embeddings/\n/indexes/\n/exports/\n/versions/\n/facts.json\n/.restore-journal.json\n**/*.tmp\n/*.lock\n# <<< Scrinia managed: project-git derived files <<<\n# <<< Scrinia managed: project-git derived files >>>\n")]
    public void ExactBlockDoesNotHideAnInvalidMarkerStructure(string content)
    {
        ManagedIgnoreBlock.Validate("user\n" + content, TrackingPolicy.ProjectGit)
            .Should().Be(ManagedIgnoreValidation.Drift);

        Action apply = () => ManagedIgnoreBlock.Apply("user\n" + content, TrackingPolicy.ProjectGit);
        apply.Should().Throw<ScriniaConfigurationException>();
    }
}
