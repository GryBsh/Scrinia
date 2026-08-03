using Scrinia.Commands.Hooks;

namespace Scrinia.Tests;

public class AgentHookSetupTests
{
    [Fact]
    public void StampWorkspaceRoot_AppendsQuotedRoot()
    {
        var spec = new HookSpec("UserPromptSubmit", "scri hint");

        var stamped = AgentHookSetup.StampWorkspaceRoot(spec, @"C:/repos/proj");

        Assert.Equal(@"scri hint --workspace-root ""C:/repos/proj""", stamped.Command);
        Assert.Equal("UserPromptSubmit", stamped.EventName);
    }

    [Fact]
    public void StampWorkspaceRoot_HandlesRootsContainingSpaces()
    {
        var spec = new HookSpec("SessionStart", "scri restore --hook");

        var stamped = AgentHookSetup.StampWorkspaceRoot(spec, @"C:/Program Files/proj");

        Assert.Contains(@"--workspace-root ""C:/Program Files/proj""", stamped.Command);
    }
}
