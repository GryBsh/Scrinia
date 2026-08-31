using System.Text.Json;
using System.Reflection;
using Scrinia.Commands;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Spectre.Console;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class CliAddressValidationTests
{
    public static TheoryData<string, string> InvalidAddresses => new()
    {
        { "file:///tmp/memory", "UnsupportedInputForm" },
        { "https://example.test/memory", "UnsupportedInputForm" },
        { "//host/memory", "UnsupportedInputForm" },
        { "/memory?chunk=1", "UnsupportedInputForm" },
        { "/memory#chunk", "UnsupportedInputForm" },
        { "/memory/%2e%2e/secret", "MalformedAddress" },
        { @"C:\memory", "MalformedAddress" },
        { "/memory:child", "MalformedAddress" },
        { "/memory/../secret", "MalformedAddress" },
        { "/", "MalformedAddress" },
    };

    public static TheoryData<string> ForbiddenAddresses => new() { "~scratch", "/temp/scratch" };

    [Theory]
    [InlineData("file:///tmp/memory", "UnsupportedInputForm")]
    [InlineData("/memory?chunk=1", "UnsupportedInputForm")]
    [InlineData("/", "MalformedAddress")]
    public async Task Show_RejectsInvalidAddressWithStableCategory(string input, string category)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var output = new StringWriter();
            TextWriter previous = Console.Out;
            Console.SetOut(output);
            try
            {
                int exit = await new ScriniaCommands().Show(input, workspaceRoot: workspace, json: true);
                Assert.Equal(1, exit);
            }
            finally
            {
                Console.SetOut(previous);
            }

            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.Equal(category, document.RootElement.GetProperty("category").GetString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAddresses))]
    public async Task EveryMemoryCommandRejectsInvalidAddressBeforeCore(string input, string category)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string contentFile = Path.Combine(workspace, "content.md");
        await File.WriteAllTextAsync(contentFile, "content");
        try
        {
            foreach (Func<Task<int>> command in InvalidAddressCommands(input, workspace, contentFile))
            {
                var (exit, output) = await CaptureJsonAsync(command);
                Assert.Equal(1, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.Equal(category, document.RootElement.GetProperty("category").GetString());
                Assert.NotEqual("", document.RootElement.GetProperty("code").GetString());
            }
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenAddresses))]
    public async Task EveryMemoryCommandRejectsEphemeralAddressAfterCanonicalization(string input)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string contentFile = Path.Combine(workspace, "content.md");
        await File.WriteAllTextAsync(contentFile, "content");
        try
        {
            foreach (Func<Task<int>> command in InvalidAddressCommands(input, workspace, contentFile))
            {
                var (exit, output) = await CaptureJsonAsync(command);
                Assert.Equal(1, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.Equal("ForbiddenAddressKind", document.RootElement.GetProperty("category").GetString());
                Assert.Equal("forbidden_address_kind", document.RootElement.GetProperty("code").GetString());
            }
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Store_TextModePreservesCoreWriteCategoryAndValidationDetail()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string contentFile = Path.Combine(workspace, "content.md");
        await File.WriteAllTextAsync(contentFile, "content");
        string subject = new('x', MemoryWrite.MaxPersistentSubjectLength + 1);
        try
        {
            var (exit, output) = await CaptureTextAsync(() =>
                new ScriniaCommands().Store(subject, contentFile, workspace, json: false));
            Assert.Equal(1, exit);
            Assert.Contains("category=InvalidWriteContent", output, StringComparison.Ordinal);
            Assert.Contains("code=invalid_write_content", output, StringComparison.Ordinal);
            Assert.Contains("182", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void FormatCliErrorPreservesCategoryCodeAndValidationDetails()
    {
        string formatted = ScriniaCommands.FormatCliError(
            ResponseBuilder.Error("Content chunk exceeds its UTF-8 byte limit.", "INVALID_PARAMETER", "Use a smaller chunk."),
            new MemoryError(MemoryErrorCategory.InvalidWriteContent, "content_chunk_too_large",
                "Content chunk exceeds its UTF-8 byte limit.", "content[0]", 5, 8));

        Assert.Contains("category=InvalidWriteContent", formatted, StringComparison.Ordinal);
        Assert.Contains("code=content_chunk_too_large", formatted, StringComparison.Ordinal);
        Assert.Contains("field=content[0]", formatted, StringComparison.Ordinal);
        Assert.Contains("limit=5", formatted, StringComparison.Ordinal);
        Assert.Contains("actual=8", formatted, StringComparison.Ordinal);
        Assert.Contains("Use a smaller chunk.", formatted, StringComparison.Ordinal);
    }

    private static IEnumerable<Func<Task<int>>> InvalidAddressCommands(string input, string workspace, string contentFile) =>
    [
        () => new ScriniaCommands().Show(input, workspaceRoot: workspace, json: true),
        () => new ScriniaCommands().Forget(input, workspaceRoot: workspace, json: true),
        () => new ScriniaCommands().Append(input, contentFile, workspace, json: true),
        () => new ScriniaCommands().Compact(input, workspaceRoot: workspace, json: true),
        () => new ScriniaCommands().Store(input, contentFile, workspace, json: true),
        () => new ScriniaCommands().Link(input, "/other", workspaceRoot: workspace, json: true),
    ];

    [Fact]
    public void MemoryCommandsUseTypedCoreAddressParameters()
    {
        string[] operationNames = ["Remember", "Recall", "Forget", "Append", "Compact", "Link"];
        foreach (MethodInfo method in typeof(Scrinia.Mcp.ScriniaMcpTools).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic).Where(method => operationNames.Contains(method.Name)))
        {
            foreach (ParameterInfo parameter in method.GetParameters().Where(parameter =>
                parameter.Name is "name" or "nameOrUri" or "from" or "to"))
                Assert.Equal(typeof(MemoryAddress), parameter.ParameterType);
        }
    }

    private static async Task<(int Exit, string Output)> CaptureJsonAsync(Func<Task<int>> command)
    {
        using var output = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(output);
        try
        {
            return (await command(), output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static async Task<(int Exit, string Output)> CaptureTextAsync(Func<Task<int>> command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        TextWriter previousOut = Console.Out;
        TextWriter previousError = Console.Error;
        IAnsiConsole previousAnsiConsole = AnsiConsole.Console;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(stdout)
        });
        try
        {
            int exit = await command();
            return (exit, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            AnsiConsole.Console = previousAnsiConsole;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
