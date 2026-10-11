using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Scrinia.Agent.Benchmarks;

public static class ModelClientFactory
{
    public static IChatClient Create(ModelSettings settings) => Create(settings, null);
    internal static IChatClient Create(ModelSettings settings, HttpClient? transport)
    {
        var secret = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"Set the nonempty credential variable {settings.ApiKeyEnvironmentVariable} before running inference.");
        var options = new OpenAIClientOptions { Endpoint = settings.Endpoint, RetryPolicy = new ClientRetryPolicy(0) };
        if (transport is not null) options.Transport = new HttpClientPipelineTransport(transport);
        return new OpenAIClient(new ApiKeyCredential(secret), options).GetChatClient(settings.Model).AsIChatClient();
    }
}
