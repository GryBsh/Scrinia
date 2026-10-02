using FluentAssertions;
using Scrinia.Core.Embeddings;
using Xunit;

namespace Scrinia.Tests;

public sealed class VectorCandidateLookupTests
{
    [Fact]
    public async Task GetCandidateVectors_IsCaseInsensitiveAndExcludesUnrequestedNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-vector-candidates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var vectors = new VectorStore(root))
            {
                await vectors.UpsertAsync("local", "Alpha", null, [1f, 0f]);
                await vectors.UpsertAsync("local", "Beta", null, [0f, 1f]);
            }

            using var reopened = new VectorStore(root);
            reopened.GetCandidateVectors("local", ["alpha"])
                .Select(vector => vector.Name)
                .Should().Equal("Alpha");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
