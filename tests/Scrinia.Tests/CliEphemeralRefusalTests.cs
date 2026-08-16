using Scrinia.Commands;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Review finding C1: `scri store /temp/x` reported success and exit 0 while discarding the
/// content — the ephemeral map is an in-memory dictionary and the CLI process exits
/// immediately. Every ephemeral CLI operation is meaningless for the same reason, so all six
/// commands refuse, not only the writes.
/// </summary>
// GlobalWorkspaceConfigCollection: every test here drives a ScriniaCommands entry point
// (Store/Append/Show/Forget/Compact/Link), each of which calls WorkspaceSetup.Configure — the
// process-wide static root this collection serializes. Tests also redirect the process-wide
// Console.Out around each call (see RunAsync); without this collection, a class outside it
// (e.g. FactsToolTests) can run its own Console.SetOut/restore concurrently and either class
// ends up reading the other's captured output.
[Collection(GlobalWorkspaceConfigCollection.Name)]
public class CliEphemeralRefusalTests
{
    private static async Task<(int Exit, string Output)> RunAsync(Func<Task<int>> command)
    {
        var captured = new StringWriter();
        TextWriter previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            int exit = await command();
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static string NewWorkspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A real <c>Store</c> through the CLI path fires its event-sink indexing detached
    /// (<c>BackgroundWork.Fire</c>) — only <c>Program.cs</c>'s real process exit drains it before
    /// the directory could be deleted. Called directly here (bypassing <c>Program.Main</c>), a
    /// test can otherwise delete its workspace while that indexing still holds
    /// <c>vectors.bin.lock</c> open, failing cleanup with an <see cref="IOException"/>. Mirrors
    /// <c>Program.cs</c>'s own drain before any test that performed a genuine persistent write
    /// deletes its temp directory.
    /// </summary>
    private static Task DrainBackgroundWorkAsync() => BackgroundWork.DrainAsync(TimeSpan.FromSeconds(10));

    public static TheoryData<string> EphemeralAddresses => new() { "~scratch", "/temp/scratch" };

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Store_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Store(address, file: null, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Append_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Append(address, file: null, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Show_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Show(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Forget_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Forget(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);

            // NOT a generic "ephemeral" substring check: without the guard, Forget("~scratch")
            // falls through to MemoryTools.Core.cs's own ephemeral-not-found path ("No ephemeral
            // memory found with name '~scratch'."), which also contains "ephemeral" and would
            // pass this assertion for the wrong reason. "names an ephemeral memory" is text only
            // RefuseEphemeral's message produces, so this only passes when the guard actually ran.
            Assert.Contains("names an ephemeral memory", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Compact_RefusesAnEphemeralAddress(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (exit, output) = await RunAsync(() =>
                new ScriniaCommands().Compact(address, workspaceRoot: dir, json: true));

            Assert.Equal(1, exit);
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The counterpart must be a REAL, existing persistent memory, not a made-up name like
    /// "/findings/x". Link's own existence check (<c>MemoryTools.Core.cs</c>'s <c>Exists</c>,
    /// checked before either side is written) fails closed on a missing persistent side
    /// regardless of whether the ephemeral guard ever ran — with a fake counterpart, both
    /// permutations return exit 1 whether or not the guard fired, for the unrelated reason that
    /// the other side doesn't exist (and for the "to" direction, the fake counterpart missing
    /// on "from" short-circuits the check so "to" is never even evaluated). Storing a real
    /// counterpart first, and asserting on the guard's own message text rather than only the
    /// exit code, is what actually proves the guard — not Link's not-found path — is what fired.
    /// </summary>
    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Link_RefusesAnEphemeralAddressOnEitherEnd(string address)
    {
        string dir = NewWorkspace();
        string contentFile = Path.Combine(dir, "counterpart.md");
        await File.WriteAllTextAsync(contentFile, "real counterpart content");
        try
        {
            var (storeExit, _) = await RunAsync(() =>
                new ScriniaCommands().Store("/findings/counterpart", file: contentFile, workspaceRoot: dir, json: true));
            Assert.Equal(0, storeExit);

            var (fromExit, fromOutput) = await RunAsync(() =>
                new ScriniaCommands().Link(address, "/findings/counterpart", workspaceRoot: dir, json: true));
            var (toExit, toOutput) = await RunAsync(() =>
                new ScriniaCommands().Link("/findings/counterpart", address, workspaceRoot: dir, json: true));

            Assert.Equal(1, fromExit);
            Assert.Equal(1, toExit);
            Assert.Contains("names an ephemeral memory", fromOutput, StringComparison.Ordinal);
            Assert.Contains("names an ephemeral memory", toOutput, StringComparison.Ordinal);
        }
        finally
        {
            await DrainBackgroundWorkAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The refusal names the canonical form, not the caller's spelling (D-3).</summary>
    [Fact]
    public async Task TheRefusalNamesTheCanonicalForm()
    {
        string dir = NewWorkspace();
        try
        {
            var (_, output) = await RunAsync(() =>
                new ScriniaCommands().Store("/temp/scratch", file: null, workspaceRoot: dir, json: true));

            Assert.Contains("~scratch", output, StringComparison.Ordinal);
            Assert.DoesNotContain("/temp/scratch", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The guard must not over-refuse. Passes a real file because Store reads stdin when
    /// file is null, which would block the test host.
    /// </summary>
    [Fact]
    public async Task APersistentAddressIsNotRefused()
    {
        string dir = NewWorkspace();
        string contentFile = Path.Combine(dir, "content.md");
        await File.WriteAllTextAsync(contentFile, "real content");
        try
        {
            var (exit, _) = await RunAsync(() =>
                new ScriniaCommands().Store("/findings/real", file: contentFile, workspaceRoot: dir, json: true));

            Assert.Equal(0, exit);
        }
        finally
        {
            await DrainBackgroundWorkAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}
