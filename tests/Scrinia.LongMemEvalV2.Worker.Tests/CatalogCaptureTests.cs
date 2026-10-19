using Microsoft.Extensions.AI;
using System.Text.Json;
using Scrinia.Agent.Benchmarks;
using Scrinia.LongMemEvalV2.Worker;
using Scrinia.LongMemEvalV2.Worker.Protocol;

namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class CatalogCaptureTests
{
    [Fact]
    public async Task Capture_uses_runtime_canonical_catalog_and_complete_guide_without_model_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "v2-capture-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(AppContext.BaseDirectory, "scrinia-payload", OperatingSystem.IsWindows() ? "scri.exe" : "scri");
        try
        {
            await CatalogCapture.RunAsync(new CatalogCaptureRequest {
                Root = root, Executable = executable, PrefixArguments = []
            }, CancellationToken.None);
            var tools = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "tools.json")), WorkerJsonContext.Default.ToolArray)!;
            Assert.Contains(tools, t => t.Name == "guide");
            Assert.Contains(tools, t => t.Name == "remember");
            Assert.All(tools, t => Assert.True(t.InputSchema.ValueKind == JsonValueKind.Object));
            var text = File.ReadAllText(Path.Combine(root, "guide.txt"));

            Assert.Contains("scrinia", text!, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidDataException>(() => CatalogCapture.RunAsync(
                new CatalogCaptureRequest { Root = root, Executable = executable, PrefixArguments = [] }, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Actual_controller_wire_preserves_explicit_thinking_image_and_independent_output_limit()
    {
        var variable = "V2_CAPTURE_WIRE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "offline-placeholder");
        try
        {
            using var handler = new WireHandler();
            using var http = new HttpClient(handler);
            var settings = new ModelSettings(new Uri("http://localhost:1/v1"), "fixture", variable, 0.6f, 16384, 0.95f, 20);
            await using var events = new WireEvents();
            using var observed = new ObservedChatClient(ModelClientFactory.Create(settings, http), settings,
                new RunLimits(40, 300, 300, 5), events, "learn", enableThinking: true);
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jM9sAAAAASUVORK5CYII=");
            await observed.GetResponseAsync([new ChatMessage(ChatRole.User,
                [new TextContent("Invented image"), new DataContent(bytes, "image/png")])]);
            using var document = JsonDocument.Parse(handler.Body!);
            var root = document.RootElement;
            Assert.True(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(16384, root.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(root.TryGetProperty("max_tokens", out _));
            Assert.Equal(20, root.GetProperty("top_k").GetInt32());
            Assert.Contains("data:image/png;base64," + Convert.ToBase64String(bytes), handler.Body);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class WireEvents : IRunEvents
    {
        public ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WireHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("""{"id":"offline","object":"chat.completion","created":1,"model":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

}
