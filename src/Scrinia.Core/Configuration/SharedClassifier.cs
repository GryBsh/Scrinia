namespace Scrinia.Core.Configuration;

public enum DerivedPathClass
{
    Authoritative,
    EmbeddingIndexes,
    SearchIndexes,
    ExportBundles,
    ArchivedCopies,
    EntityFacts,
    RestoreJournal,
    TemporaryTree,
    RootLockTree,
}

public sealed record DerivedPathRule(
    DerivedPathClass Class,
    string IgnorePattern,
    IReadOnlyList<string> CleanupPathspecs,
    Func<string, bool> Matches);

public sealed record DerivedPathClassification(DerivedPathClass Class, DerivedPathRule? Rule)
{
    public bool IsDerived => Class != DerivedPathClass.Authoritative;
}

/// <summary>Single ordered authority for derived paths and all consumers of that classification.</summary>
public static class SharedClassifier
{
    private static readonly IReadOnlyList<DerivedPathRule> ClassifierRules =
    [
        Rule(DerivedPathClass.EmbeddingIndexes, "/embeddings/", ".scrinia/embeddings", IsRootDirectory("embeddings")),
        Rule(DerivedPathClass.SearchIndexes, "/indexes/", ".scrinia/indexes", IsRootDirectory("indexes")),
        Rule(DerivedPathClass.ExportBundles, "/exports/", ".scrinia/exports", IsRootDirectory("exports")),
        Rule(DerivedPathClass.ArchivedCopies, "/versions/", ".scrinia/versions", IsRootDirectory("versions")),
        Rule(DerivedPathClass.EntityFacts, "/facts.json", ".scrinia/facts.json", path => path == "facts.json"),
        Rule(DerivedPathClass.RestoreJournal, "/.restore-journal.json", ".scrinia/.restore-journal.json", path => path == ".restore-journal.json"),
        Rule(DerivedPathClass.TemporaryTree, "**/*.tmp", ":(glob).scrinia/*.tmp", ":(glob).scrinia/**/*.tmp", HasTmpSegment),
        Rule(DerivedPathClass.RootLockTree, "/*.lock", ":(glob).scrinia/*.lock", HasRootLockSegment),
    ];

    public static IReadOnlyList<DerivedPathRule> Rules => ClassifierRules;

    public static DerivedPathClassification Classify(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string path = relativePath.Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.StartsWith("../", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal))
            throw new ArgumentException("The classifier requires a safe relative path.", nameof(relativePath));

        DerivedPathRule? rule = ClassifierRules.FirstOrDefault(candidate => candidate.Matches(path));
        return rule is null ? new(DerivedPathClass.Authoritative, null) : new(rule.Class, rule);
    }

    public static bool IsExcludedFromFingerprint(string relativePath) => Classify(relativePath).IsDerived;

    public static IReadOnlyList<string> IgnorePatterns(DerivedPathClassification classification) =>
        classification.Rule is null ? [] : [classification.Rule.IgnorePattern];

    public static IReadOnlyList<string> CleanupPathspecs(DerivedPathClassification classification) =>
        classification.Rule?.CleanupPathspecs ?? [];

    private static DerivedPathRule Rule(DerivedPathClass @class, string ignorePattern, string cleanup, Func<string, bool> matcher) =>
        new(@class, ignorePattern, [cleanup], matcher);

    private static DerivedPathRule Rule(DerivedPathClass @class, string ignorePattern, string cleanup1, string cleanup2, Func<string, bool> matcher) =>
        new(@class, ignorePattern, [cleanup1, cleanup2], matcher);

    private static Func<string, bool> IsRootDirectory(string name) => path =>
        path == name || path.StartsWith(name + "/", StringComparison.Ordinal);

    private static bool HasTmpSegment(string path) => path.Split('/').Any(segment => segment.EndsWith(".tmp", StringComparison.Ordinal));

    private static bool HasRootLockSegment(string path)
    {
        int slash = path.IndexOf('/');
        string rootSegment = slash < 0 ? path : path[..slash];
        return rootSegment.EndsWith(".lock", StringComparison.Ordinal);
    }
}
