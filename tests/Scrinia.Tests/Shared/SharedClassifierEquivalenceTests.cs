using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 9 — the classifier is the single ordered authority for every derived-path consumer. The
/// managed ignore block, the fingerprint exclusion set, and the static cleanup pathspecs must denote
/// exactly the classifier's derived-path union: for every directory, singleton, descendant, and
/// reserved-suffix path the four views agree on derived-vs-authoritative, and the managed block and
/// cleanup argv are built from the same ordered rules. Git patterns may overlap a path while that path
/// still receives one semantic class (first match wins).
/// </summary>
public sealed class SharedClassifierEquivalenceTests
{
    private static readonly string[] ExpectedMemoriesLocalTopicReviewNmp2SkillsQaMdMetaJsonAgentProfileMdTopicWorkerLockNotesLockfileCacheTmpBak = new[]
        {
            "memories/local-topic/review.nmp2",
            "skills/qa.md.meta.json",
            "agent/profile.md",
            "topic/worker.lock",
            "notes.lockfile",
            "cache.tmp.bak",
        };

    /// <summary>
    /// For every directory, file, and descendant case the classifier, the fingerprint exclusion, the
    /// managed ignore block, and the static cleanup pathspecs agree on the derived set.
    /// </summary>
    [Theory]
    // Canonical root derived directories and their descendants (rule 1).
    [InlineData("embeddings", "EmbeddingIndexes", true)]
    [InlineData("embeddings/model.bin", "EmbeddingIndexes", true)]
    [InlineData("embeddings/nested/deep.bin", "EmbeddingIndexes", true)]
    [InlineData("indexes/search.db", "SearchIndexes", true)]
    [InlineData("indexes/sub/cache.db", "SearchIndexes", true)]
    [InlineData("exports/bundle.nmp", "ExportBundles", true)]
    [InlineData("versions/old.nmp", "ArchivedCopies", true)]
    [InlineData("versions/2026/old.nmp", "ArchivedCopies", true)]
    // Canonical root derived singleton files (rule 2).
    [InlineData("facts.json", "EntityFacts", true)]
    [InlineData(".restore-journal.json", "RestoreJournal", true)]
    // Any-depth .tmp files and descendants of a .tmp directory (rule 3).
    [InlineData("cache.tmp", "TemporaryTree", true)]
    [InlineData("topic/cache.tmp", "TemporaryTree", true)]
    [InlineData("a/b/c.tmp", "TemporaryTree", true)]
    [InlineData("cache.tmp/child.bin", "TemporaryTree", true)]
    [InlineData("topic/cache.tmp/leaf", "TemporaryTree", true)]
    // Root-child .lock files and descendants of a root-child .lock directory (rule 4).
    [InlineData("worker.lock", "RootLockTree", true)]
    [InlineData("worker.lock/child.bin", "RootLockTree", true)]
    [InlineData("ops.lock/sub/x", "RootLockTree", true)]
    // First-match precedence: a path also matching a later reserved-suffix rule keeps the earlier class.
    [InlineData("embeddings/cache.tmp", "EmbeddingIndexes", true)]
    [InlineData("indexes/worker.lock", "SearchIndexes", true)]
    [InlineData("versions/old.lock", "ArchivedCopies", true)]
    // Authoritative paths — none may match a reserved suffix or a root-derived rule.
    [InlineData("memories/local-topic/review.nmp2", "Authoritative", false)]
    [InlineData("memories/local-topic/review.nmp2.meta.json", "Authoritative", false)]
    [InlineData("skills/qa.md", "Authoritative", false)]
    [InlineData("skills/qa.md.meta.json", "Authoritative", false)]
    [InlineData("agent/profile.md", "Authoritative", false)]
    [InlineData("topics/embeddings/authoritative.nmp2", "Authoritative", false)]
    [InlineData("facts.json.meta.json", "Authoritative", false)]
    [InlineData("topic/ordinary.nmp2", "Authoritative", false)]
    [InlineData("topic/worker.lock", "Authoritative", false)]
    [InlineData("notes.lockfile", "Authoritative", false)]
    [InlineData("cache.tmp.bak", "Authoritative", false)]
    [InlineData("worker.lock.bak", "Authoritative", false)]
    public void Classifier_sets_match_for_every_directory_file_and_descendant_case(
        string relativePath, string expectedClass, bool expectedDerived)
    {
        DerivedPathClassification classification = SharedClassifier.Classify(relativePath);

        classification.Class.Should().Be(Enum.Parse<DerivedPathClass>(expectedClass));
        classification.IsDerived.Should().Be(expectedDerived);

        // Fingerprint exclusion is the same union as the classifier.
        SharedClassifier.IsExcludedFromFingerprint(relativePath).Should().Be(expectedDerived);

        bool ignored = ManagedIgnoreBlockLines.Any(line => GitignoreMatches(line, relativePath));
        bool cleaned = CleanupPathsFor(relativePath).Any();

        expectedDerived
            .Should()
            .Be(ignored, "the managed ignore block must denote the classifier's derived union")
            .And
            .Be(cleaned, "the static cleanup pathspecs must denote the classifier's derived union");

        if (expectedDerived)
        {
            SharedClassifier.IgnorePatterns(classification).Count.Should().BeGreaterThanOrEqualTo(1);
            SharedClassifier.CleanupPathspecs(classification).Count.Should().BeGreaterThanOrEqualTo(1);
        }
    }

    /// <summary>
    /// The managed ignore block and the cleanup argv are both produced from the single ordered rule set;
    /// the block lists every rule's ignore pattern in order, and every rule carries a cleanup pathspec.
    /// </summary>
    [Fact]
    public void Managed_block_and_cleanup_are_built_from_the_one_ordered_rule_set()
    {
        string block = ManagedIgnoreBlock.ProjectGit;

        foreach (DerivedPathRule rule in SharedClassifier.Rules)
            block.Should().Contain(rule.IgnorePattern);

        SharedClassifier.Rules.Should().AllSatisfy(rule =>
        {
            rule.IgnorePattern.Should().NotBeNullOrWhiteSpace();
            rule.CleanupPathspecs.Should().NotBeEmpty();
        });

        // The block is the exact authoritative derived-only region from spec §8.2.
        block.Should().Be(
            "# >>> Scrinia managed: project-git derived files >>>\n" +
            "/embeddings/\n" +
            "/indexes/\n" +
            "/exports/\n" +
            "/versions/\n" +
            "/facts.json\n" +
            "/.restore-journal.json\n" +
            "**/*.tmp\n" +
            "/*.lock\n" +
            "# <<< Scrinia managed: project-git derived files <<<\n");
    }

    /// <summary>
    /// Git patterns overlap a path while the path still receives exactly one semantic class: the first
    /// matching rule wins, and an earlier directory rule classifies a descendant even when a later
    /// reserved-suffix rule would also match it.
    /// </summary>
    [Fact]
    public void First_match_wins_when_git_patterns_overlap_a_path()
    {
        // cache.tmp under the embeddings root is covered by both /embeddings/ and **/*.tmp, yet it is
        // a single EmbeddingIndexes class by first-match precedence.
        SharedClassifier.Classify("embeddings/cache.tmp").Class.Should().Be(DerivedPathClass.EmbeddingIndexes);

        // The managed ignore block still covers it through the directory rule only.
        ManagedIgnoreBlockLines.Should().Contain("/embeddings/");
        ManagedIgnoreBlockLines.Any(line => GitignoreMatches(line, "embeddings/cache.tmp")).Should().BeTrue();

        // The cleanup pathspec covers it through the single directory pathspec, not the .tmp argv.
        CleanupPathsFor("embeddings/cache.tmp").Should().Contain(".scrinia/embeddings");
    }

    /// <summary>
    /// No authoritative path carries a reserved suffix at any depth; conversely every path with a
    /// root-level .tmp segment or a root-child .lock segment is derived. Classification is a pure string
    /// function, so it is filesystem-independent and classifies deleted-but-still-indexed paths exactly
    /// as it classifies on-disk ones.
    /// </summary>
    [Fact]
    public void Reserved_suffix_rules_are_exhaustive_and_never_leave_an_authoritative_path_derived()
    {
        // Every path string the classifier is asked to classify is derived-or-authoritative by content
        // alone; existence on disk is irrelevant (covers deleted-but-indexed paths).
        SharedClassifier.Classify("cache.tmp").Class.Should().Be(DerivedPathClass.TemporaryTree);
        SharedClassifier.Classify("a/b/c.tmp").Class.Should().Be(DerivedPathClass.TemporaryTree);

        // An authoritative path never hides a reserved suffix.
        ExpectedMemoriesLocalTopicReviewNmp2SkillsQaMdMetaJsonAgentProfileMdTopicWorkerLockNotesLockfileCacheTmpBak
        .Should()
        .AllSatisfy(path =>
            SharedClassifier.Classify(path).Class.Should().Be(DerivedPathClass.Authoritative));

        // A .tmp/.lock path is derived whether or not it exists on disk (pure function).
        SharedClassifier.IsExcludedFromFingerprint("indexed-but-deleted.tmp").Should().BeTrue();
    }

    private static readonly string[] ManagedIgnoreBlockLines =
        ManagedIgnoreBlock.ProjectGit
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

    private static IEnumerable<string> CleanupPathsFor(string relativePath)
    {
        string repoPath = ".scrinia/" + relativePath.Replace('\\', '/');
        DerivedPathRule? rule = SharedClassifier.Classify(relativePath).Rule;
        if (rule is null)
            return [];

        return rule.CleanupPathspecs
            .Where(spec => GitGlobMatches(spec.StartsWith(":(glob)") ? spec[7..] : spec, repoPath));
    }

    private static bool GitignoreMatches(string pattern, string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        pattern = pattern.Trim();
        if (pattern.Length == 0 || pattern[0] is '#' or '!')
            return false;

        bool dirOnly = pattern.EndsWith('/');
        if (dirOnly)
            pattern = pattern[..^1].Trim();

        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
        }

        bool hasWildcard = pattern.Contains('*') || pattern.Contains('?');
        bool coversDescendants = dirOnly || hasWildcard;
        string regex = "^" + ConvertGlobToRegex(pattern) + (coversDescendants ? "(/.*)?$" : "$");
        return Regex.IsMatch(path, regex);
    }

    private static bool GitGlobMatches(string pathspec, string repoRelativePath)
    {
        string body = pathspec.StartsWith(":(glob)") ? pathspec[7..] : pathspec;
        bool hasWildcard = body.Contains('*') || body.Contains('?');
        bool looksLikeDirectory = !HasFileExtension(body);
        bool coversDescendants = hasWildcard || looksLikeDirectory;
        string regex = "^" + ConvertGlobToRegex(body) + (coversDescendants ? "(/.*)?$" : "$");
        return Regex.IsMatch(repoRelativePath, regex);
    }

    private static bool HasFileExtension(string path)
    {
        int slash = path.LastIndexOf('/');
        string lastSegment = slash < 0 ? path : path[(slash + 1)..];
        return lastSegment.Contains('.');
    }

    private static string ConvertGlobToRegex(string glob)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        i += 2;
                        }
                        else
                        {
                            builder.Append(".*");
                            i++;
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}
