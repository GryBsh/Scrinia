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

    // "./" and ".//" (and their backslash equivalents on Windows) don't string-equal dirFull the
    // way "." and "" do — Path.GetFullPath preserves a trailing separator when the last segment
    // ends in one — so they need their own cases rather than relying on bare "." coverage.
    // Path.DirectorySeparatorChar-derived entries make sure the theory isn't hard-coded to one
    // separator style.
    public static IEnumerable<object[]> DegenerateConflictDirValues()
    {
        yield return new object[] { "." };
        yield return new object[] { "" };
        yield return new object[] { "./" };
        yield return new object[] { ".//" };
        yield return new object[] { "." + Path.DirectorySeparatorChar };
        yield return new object[] { "." + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar };
    }

    [Theory]
    [MemberData(nameof(DegenerateConflictDirValues))]
    public void DegenerateConflictDir_Throws_AndWritesNoGitignore(string conflictDir)
    {
        // "." and "" both resolve Path.Combine(dirFull, x) back to dirFull. IsWithin treated
        // equality as containment, so the guard passed and the driver wrote a .gitignore of "*"
        // into the artifact directory — silently untracking every memory at and below it.
        // Asserting the absence matters more than the throw: the .gitignore write is the harm.
        // "./" resolves to dirFull + separator instead — a distinct string from dirFull — and
        // must be caught by trimming the trailing separator before the equality check.
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        string gitignorePath = Path.Combine(dir, ".gitignore");
        const string preexistingContent = "existing-user-content\n";
        File.WriteAllText(gitignorePath, preexistingContent);
        var config = new MergeConfig(ConflictDir: conflictDir);

        Action act = () => Nmp2ConflictHandler.Handle(ancestor, ours, theirs, ours, config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConflictDir*");
        File.ReadAllText(gitignorePath).Should().Be(preexistingContent);
        Directory.Exists(Path.Combine(dir, "current")).Should().BeFalse();
        Directory.Exists(Path.Combine(dir, "incoming")).Should().BeFalse();
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

    // Regression guard: these are all legitimate, non-degenerate ConflictDir values that must
    // keep resolving to a real subdirectory of the artifact directory. Pins the strictness of
    // IsStrictlyWithin so a future edit can't tighten the guard further by accident.
    [Theory]
    [InlineData("conflict")]
    [InlineData("./conflict")]
    [InlineData("conflict/")]
    [InlineData("conflict/.")]
    public void AllowedConflictDir_IsAccepted(string conflictDir)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string ancestor = Path.Combine(dir, "a.nmp2"); File.WriteAllText(ancestor, "ANC");
        string ours = Path.Combine(dir, "ours.nmp2"); File.WriteAllText(ours, "OURS");
        string theirs = Path.Combine(dir, "theirs.nmp2"); File.WriteAllText(theirs, "THEIRS");
        var config = new MergeConfig(ConflictDir: conflictDir);

        Nmp2ConflictHandler.Handle(ancestor, ours, theirs, ours, config);
        Directory.Exists(Path.Combine(dir, "conflict", "current")).Should().BeTrue();
    }
}
