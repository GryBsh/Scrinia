using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Scrinia.Agent.Benchmarks;

public static class ModelClientFactory
{
    // Some OpenAI-compatible servers support top_k, which is not a modeled OpenAI field.
    // Use the SDK extension point so its normal serialization and tool loop remain intact.
    internal static OpenAI.Chat.ChatCompletionOptions CreateSamplingOptions(int topK)
    {
        var options = new OpenAI.Chat.ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is the pinned SDK's additional-property API.
        options.Patch.Set("$.top_k"u8, topK);
#pragma warning restore SCME0001
        return options;
    }

    public static IChatClient Create(ModelSettings settings) => Create(settings, null);
    internal static IChatClient Create(ModelSettings settings, HttpClient? transport)
    {
        var secret = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret))
            throw InputDiagnostics.Mark(new InvalidOperationException("A model credential is missing."),
                $"Set the nonempty credential variable {settings.ApiKeyEnvironmentVariable} before running inference.");
        var options = new OpenAIClientOptions { Endpoint = settings.Endpoint, RetryPolicy = new ClientRetryPolicy(0) };
        if (transport is not null) options.Transport = new HttpClientPipelineTransport(transport);
        return new OpenAIClient(new ApiKeyCredential(secret), options).GetChatClient(settings.Model).AsIChatClient();
    }
}
