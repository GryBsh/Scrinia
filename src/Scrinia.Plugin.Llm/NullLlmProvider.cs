namespace Scrinia.Plugin.Llm;

/// <summary>
/// No-op fallback used when no GGUF model is on disk yet. Keeps the plugin process
/// responsive (status calls still answer) so the host can report a clear setup hint
/// rather than seeing a dead subprocess.
/// </summary>
public sealed class NullLlmProvider : ILocalLlm
{
    public bool IsAvailable => false;
    public string ModelPath => "";
    public string ModelArchitecture => "none";
    public string Hardware => "none";
    public string? LastError { get; }

    public NullLlmProvider(string? lastError = null) { LastError = lastError; }

    public Task<string?> CompleteAsync(
        string system, string user, int maxTokens, double temperature,
        IReadOnlyList<string>? stopSequences, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
