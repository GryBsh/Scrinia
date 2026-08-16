using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// The "/temp/" surface alias. These pin the rule itself; the round-trip behaviour it enables
/// is pinned in EphemeralAliasRoundTripTests.
/// </summary>
public class EphemeralAliasTests
{
    [Theory]
    [InlineData("/temp/x", "~x")]
    [InlineData("/temp/scratch", "~scratch")]
    [InlineData("/temp/a/b", "~a/b")]
    [InlineData("/TEMP/x", "~x")]
    [InlineData("/Temp/Probe", "~Probe")]   // prefix folds case; the subject does NOT
    public void CanonicalizeEphemeralAlias_RewritesTempPrefix(string input, string expected)
        => Assert.Equal(expected, MemoryNaming.CanonicalizeEphemeralAlias(input));

    [Theory]
    [InlineData("~x")]
    [InlineData("/temp")]              // no trailing slash — an ordinary persistent path
    [InlineData("/tempest/x")]
    [InlineData("/findings/SEC-001")]
    [InlineData("temp/x")]             // no leading slash
    [InlineData("")]
    public void CanonicalizeEphemeralAlias_LeavesEverythingElseAlone(string input)
        => Assert.Equal(input, MemoryNaming.CanonicalizeEphemeralAlias(input));

    [Theory]
    [InlineData("/temp/x", true)]
    [InlineData("/TEMP/x", true)]
    [InlineData("/temp", false)]
    [InlineData("/tempest/x", false)]
    [InlineData("~x", false)]          // canonical form is NOT the alias
    [InlineData("", false)]
    public void IsEphemeralAlias_MatchesOnlyTheTempPrefix(string input, bool expected)
        => Assert.Equal(expected, MemoryNaming.IsEphemeralAlias(input));

    [Theory]
    [InlineData("~x", true)]
    [InlineData("~", true)]
    [InlineData("/temp/x", false)]     // an alias is not yet an ephemeral NAME
    [InlineData("/findings/a", false)]
    [InlineData("", false)]
    public void IsEphemeralName_MatchesOnlyTheTildeForm(string input, bool expected)
        => Assert.Equal(expected, MemoryNaming.IsEphemeralName(input));

    /// <summary>
    /// The delegation in Step 4 must be behaviour-preserving, and the body it replaces is
    /// null-safe. Pin that.
    /// </summary>
    [Fact]
    public void IsEphemeralName_IsNullSafe()
        => Assert.False(MemoryNaming.IsEphemeralName(null!));

    /// <summary>
    /// D-4: the store predicate stays "~"-only. If this goes red, a surface alias has leaked
    /// into the store layer and the design has been violated.
    /// </summary>
    [Fact]
    public void FileMemoryStore_IsEphemeral_DoesNotKnowTheAlias()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileMemoryStore(dir);
            Assert.True(store.IsEphemeral("~x"));
            Assert.False(store.IsEphemeral("/temp/x"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
