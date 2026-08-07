using Scrinia.Merge;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class ConflictDirContainmentTests
{
    [Theory]
    [InlineData("../../../escape")]
    [InlineData("/abs/escape")]
    public void OutOfBoundsConflictDir_Throws(string conflictDir)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Three distinct files so neither trivial-merge branch short-circuits.
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        var config = new MergeConfig(ConflictDir: conflictDir);

        Action act = () => Nmp2ConflictHandler.Handle(ancestor, ours, theirs, ours, config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RelativeConflictDir_IsAllowed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        var config = new MergeConfig(); // default ConflictDir = "conflict"

        Nmp2ConflictHandler.Handle(ancestor, ours, theirs, ours, config);
        Directory.Exists(Path.Combine(dir, "conflict", "current")).Should().BeTrue();
    }
}
