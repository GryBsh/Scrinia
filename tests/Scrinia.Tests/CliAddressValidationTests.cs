using System.Text.Json;
using Scrinia.Commands;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class CliAddressValidationTests
{
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
}
