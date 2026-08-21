using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Formats;

public class RegisteredFormatsTests
{
    /// <summary>
    /// facts.json rebuilds on ANY version that is not Current — older as well as newer. That
    /// asymmetry is deliberate: its version tracks the derivation RULE, not the file shape, so a
    /// cache written by a previous build is a path around the new rule.
    ///
    /// <para>Versions are computed from Current rather than hard-coded. Literal 2 and 4 against a
    /// Current of 3 turn into a FALSE FAILURE the moment the derivation rule is bumped to 4 —
    /// the exact event this test exists to keep safe.</para>
    /// </summary>
    [Fact]
    public void FactsSidecar_RebuildsOnAnyVersionThatIsNotCurrent()
    {
        int current = DurableFormats.EntityFacts.Current;
        foreach (int version in new[] { current - 1, current + 1, 0 })
        {
            string json = $$"""{"version":{{version}},"token":"t","memories":[]}""";
            EntityFactSidecar.TryLoad(json).Should().BeNull(
                $"version {version} is not {current}, and a cache from any other build " +
                "predates or postdates this one's derivation rule");
        }
    }

    [Fact]
    public void FactsSidecar_LoadsAtTheCurrentVersion()
    {
        string json = $$"""
        {"version":{{DurableFormats.EntityFacts.Current}},"token":"t","memories":[]}
        """;
        EntityFactSidecar.TryLoad(json).Should().NotBeNull();
    }

    [Fact]
    public void FactsSidecarVersionConstant_MatchesItsContract() =>
        EntityFactSidecar.Version.Should().Be(DurableFormats.EntityFacts.Current);

    // CanDecode (Nmp2Strategy.cs:205-207) requires BOTH the "NMP/2 " prefix AND an "NMP/END"
    // marker, so a fixture carrying only the header is rejected and would make the true case
    // fail. Build these from a real encode where possible rather than by hand.
    [Theory]
    [InlineData("NMP/2 5B CRC32:00000000\n<payload>\nNMP/END", true)]
    [InlineData("NMP/2 5B CRC32:00000000", false)]
    [InlineData("NMP/1 5B CRC32:00000000\n<payload>\nNMP/END", false)]
    [InlineData("plain text, no sentinel", false)]
    public void Nmp2Parser_MatchesItsDeclaredContract(string header, bool shouldParse)
    {
        // Conformance only. The parser is NOT routed through the gate -- its sentinel check
        // already enforces this and it is a hot, heavily-tested path. This test is what keeps
        // the registry's claim about it honest.
        DurableFormats.Nmp2Body.OnOlder.Should().Be(FormatDisposition.Refuse);
        DurableFormats.Nmp2Body.OnNewer.Should().Be(FormatDisposition.Refuse);

        Nmp2Strategy.Instance.CanDecode(header).Should().Be(shouldParse);
    }
}
