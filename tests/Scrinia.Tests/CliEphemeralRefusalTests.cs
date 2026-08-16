using Scrinia.Commands;

namespace Scrinia.Tests;

/// <summary>
/// Review finding C1: `scri store /temp/x` reported success and exit 0 while discarding the
/// content — the ephemeral map is an in-memory dictionary and the CLI process exits
/// immediately. Every ephemeral CLI operation is meaningless for the same reason, so all six
/// commands refuse, not only the writes.
/// </summary>
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
            Assert.Contains("ephemeral", output, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [MemberData(nameof(EphemeralAddresses))]
    public async Task Link_RefusesAnEphemeralAddressOnEitherEnd(string address)
    {
        string dir = NewWorkspace();
        try
        {
            var (fromExit, _) = await RunAsync(() =>
                new ScriniaCommands().Link(address, "/findings/x", workspaceRoot: dir, json: true));
            var (toExit, _) = await RunAsync(() =>
                new ScriniaCommands().Link("/findings/x", address, workspaceRoot: dir, json: true));

            Assert.Equal(1, fromExit);
            Assert.Equal(1, toExit);
        }
        finally { Directory.Delete(dir, recursive: true); }
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
        finally { Directory.Delete(dir, recursive: true); }
    }
}
