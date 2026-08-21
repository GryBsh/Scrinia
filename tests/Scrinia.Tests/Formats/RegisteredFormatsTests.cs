using FluentAssertions;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Search;

namespace Scrinia.Tests.Formats;

public class RegisteredFormatsTests
{
    [Theory]
    [InlineData(2)]  // older
    [InlineData(4)]  // newer
    [InlineData(0)]  // absent -> deserializes to 0
    public void FactsSidecar_RebuildsOnAnyVersionThatIsNotCurrent(int version)
    {
        // Behaviour must be IDENTICAL to the pre-refactor inline `dto.Version != Version`
        // check. Rebuilding on OLDER is deliberate: this version tracks the derivation rule,
        // so a cache written by a previous build is a path around the new rule.
        string json = $$"""{"version":{{version}},"token":"t","memories":[]}""";
        EntityFactSidecar.TryLoad(json).Should().BeNull();
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
