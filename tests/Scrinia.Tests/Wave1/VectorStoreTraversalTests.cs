using System.IO;
using System.Reflection;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests.Wave1;

public class VectorStoreTraversalTests : IDisposable
{
    private readonly string _tempRoot;

    public VectorStoreTraversalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"scrinia-w1-vec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../../etc/passwd")]
    [InlineData("local:../../etc")]
    [InlineData("/abs/path")]
    [InlineData("C:\\windows")]
    [InlineData("local:foo/../bar")]
    public void ScopeToRelativeDir_RejectsTraversalScopes(string scope)
    {
        var store = new VectorStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // TryBuildScopedTopicScope gate: any scope containing .. or absolute segment must fail the single constructor.
        // For scopes with "local:" prefix, extract topic part for validation.
        string topicForValidation = scope.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
            ? scope["local:".Length..]
            : scope;

        // For bare "local" / "ephemeral" we don't validate via TryBuild; for others we do.
        // Leading-slash topics like "/abs/path" are deliberately allowed by MemoryNaming
        // (it collapses redundant separators — see DualModeParseTests), so we only assert
        // TryBuild failure for traversal-containing topics, not for absolute-path topics.
        if (!scope.Equals("local", StringComparison.OrdinalIgnoreCase) && !scope.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(topicForValidation) && topicForValidation.Contains("..", StringComparison.Ordinal))
            {
                bool ok = MemoryNaming.TryBuildScopedTopicScope(topicForValidation, out _);
                Assert.False(ok, $"Scope '{scope}' should be rejected by TryBuildScopedTopicScope (topic part '{topicForValidation}')");
            }
        }

        // Also assert direct GetVectors never escapes baseDir when called via public surface:
        // For a traversal-like scope that somehow reaches GetFilePath, it must throw ArgumentException.
        if (scope.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(scope) || scope.Contains(':') && !scope.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Throws<ArgumentException>(() => store.GetVectors(scope));
        }
        else if (scope.Contains("..", StringComparison.Ordinal) || scope.StartsWith("/", StringComparison.Ordinal) || scope.StartsWith("\\", StringComparison.Ordinal))
        {
            Assert.Throws<ArgumentException>(() => store.GetVectors(scope));
        }
        else
        {
            // For the absolute-path cases that are not caught by Contains("..") or IsPathRooted on this OS,
            // still require rejection via GetVectors.
            Assert.Throws<ArgumentException>(() => store.GetVectors(scope));
        }
    }

    [Fact]
    public void GetFilePath_NeverEscapesBaseDir()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "scrinia-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var store = new VectorStore(baseDir);
        try
        {
            foreach (var scope in new[] { "local", "local:topic", "ephemeral" })
            {
                string path = (string)store.GetType().GetMethod("GetFilePath",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(store, new object[] { scope })!;

                Assert.True(PathContainment.IsWithin(baseDir, path),
                    $"GetFilePath('{scope}') escaped baseDir: {path}");

                // Also via full-path StartsWith fallback
                string fullBase = Path.GetFullPath(baseDir);
                string fullPath = Path.GetFullPath(path);
                Assert.True(fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase),
                    $"GetFilePath('{scope}') escaped baseDir via StartsWith: {path}");
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidScopes_StillWork()
    {
        string baseDir = Path.Combine(_tempRoot, "valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var store = new VectorStore(baseDir);

        // These should NOT throw and should stay within baseDir
        foreach (var scope in new[] { "local", "local:topic", "local:foo/bar", "ephemeral", "local:my-topic_123" })
        {
            var ex = Record.Exception(() => store.GetVectors(scope));
            Assert.Null(ex);

            string path = (string)store.GetType().GetMethod("GetFilePath",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(store, new object[] { scope })!;
            Assert.True(PathContainment.IsWithin(baseDir, path), $"Valid scope '{scope}' should stay within baseDir but got {path}");
        }
    }

    [Fact]
    public void LocalTraversalScope_ThrowsFromVectorStore()
    {
        var store = new VectorStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => store.GetVectors("local:../../etc"));
    }

    [Fact]
    public void DotDotSegment_Rejected()
    {
        var store = new VectorStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => store.GetVectors(".."));
    }

    [Fact]
    public void AbsolutePath_Rejected()
    {
        var store = new VectorStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        // Absolute path must be rejected regardless of OS
        Assert.Throws<ArgumentException>(() => store.GetVectors("/abs/path"));
        Assert.Throws<ArgumentException>(() => store.GetVectors("C:\\windows"));
    }

    [Theory]
    [InlineData("local:topic.")]
    [InlineData("local:topic ")]
    [InlineData("local:../../escape")]
    public void ScopeIdentityChangingOrEscapingInput_IsRejected(string scope)
    {
        var store = new VectorStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.Throws<ArgumentException>(() => store.GetVectors(scope));
    }
}
