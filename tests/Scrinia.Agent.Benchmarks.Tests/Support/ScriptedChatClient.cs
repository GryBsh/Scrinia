using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

internal sealed class ScriptedChatClient(params object[] script) : IChatClient
{
    private readonly Queue<object> _script = new(script);
    public List<(ChatMessage[] Messages, ChatOptions? Options)> Requests { get; } = [];
    public bool Disposed { get; private set; }
    public Action? OnRequest { get; set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((messages.Select(m => new ChatMessage(m.Role, m.Contents.ToList())).ToArray(), options?.Clone()));
        OnRequest?.Invoke();
        var next = _script.Dequeue();
        return next is Exception error ? Task.FromException<ChatResponse>(error) : Task.FromResult((ChatResponse)next);
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(ChatClientMetadata)
        ? new ChatClientMetadata("offline-script", new Uri("http://localhost"), "reported-fixture-model") : null;
    public void Dispose() => Disposed = true;
    public static ChatResponse Text(string text, UsageDetails? usage = null) => new(new ChatMessage(ChatRole.Assistant, text))
        { FinishReason = ChatFinishReason.Stop, ModelId = "reported-fixture-model", Usage = usage };
    public static ChatResponse Call(string id, string name, Dictionary<string, object?> arguments, string? text = null) => new(new ChatMessage(ChatRole.Assistant,
        text is null ? [new FunctionCallContent(id, name, arguments)] : [new TextContent(text), new FunctionCallContent(id, name, arguments)]))
        { FinishReason = ChatFinishReason.ToolCalls, ModelId = "reported-fixture-model" };
}

internal sealed class CapturingEvents : IRunEvents
{
    public List<(string Phase, string Kind, JsonElement Data)> Items { get; } = [];
    public bool Fail { get; set; }
    public string? FailOnceOnKind { get; set; }
    public ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Fail) throw new IOException("Injected evidence failure.");
        if (kind == FailOnceOnKind) { FailOnceOnKind = null; throw new IOException("Injected tool evidence failure."); }
        Items.Add((phase, kind, data.Clone()));
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
