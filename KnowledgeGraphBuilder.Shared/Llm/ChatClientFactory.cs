using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace KnowledgeGraphBuilder.Shared.Llm;

public enum LlmProvider { Anthropic, OpenAI, AzureOpenAI, Ollama, Fake }

public sealed record LlmOptions(LlmProvider Provider, string Model, string? Endpoint, string? ApiKey);

public static class LlmDefaults
{
    public const LlmProvider Provider = LlmProvider.Anthropic;
    public const string AnthropicModel = "claude-sonnet-5-5";

    public static string DefaultModel(LlmProvider provider) => provider switch
    {
        LlmProvider.Anthropic => AnthropicModel,
        LlmProvider.OpenAI => "gpt-4.1",
        LlmProvider.AzureOpenAI => "",
        LlmProvider.Ollama => "llama3.2",
        _ => "fake",
    };

    public static bool RequiresKey(LlmProvider provider) => provider is LlmProvider.Anthropic or LlmProvider.OpenAI or LlmProvider.AzureOpenAI;

    public static bool RequiresEndpoint(LlmProvider provider) => provider is LlmProvider.AzureOpenAI or LlmProvider.Ollama;

    public static string DisplayName(LlmProvider provider) => provider switch
    {
        LlmProvider.AzureOpenAI => "Azure OpenAI",
        _ => provider.ToString(),
    };

    /// <summary>The only host the API key is sent to.</summary>
    public static string? HostFor(LlmProvider provider, string? endpoint) => provider switch
    {
        LlmProvider.Anthropic => "api.anthropic.com",
        LlmProvider.OpenAI => "api.openai.com",
        LlmProvider.AzureOpenAI or LlmProvider.Ollama => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Authority : null,
        _ => null,
    };

    public static IEnumerable<LlmProvider> Available()
    {
        yield return LlmProvider.Anthropic;
        yield return LlmProvider.OpenAI;
        yield return LlmProvider.AzureOpenAI;
        yield return LlmProvider.Ollama;
#if DEBUG
        yield return LlmProvider.Fake;
#endif
    }
}

/// <summary>Creates provider clients that work both in the browser and on the desktop (D8).</summary>
public static class ChatClientFactory
{
    /// <exception cref="ArgumentException">A required value is missing (key; endpoint for Azure and Ollama).</exception>
    public static IChatClient Create(LlmOptions options, HttpClient http, bool isBrowser)
    {
        ArgumentNullException.ThrowIfNull(options);
        var model = options.Model?.Trim();
        if (string.IsNullOrEmpty(model) && options.Provider != LlmProvider.Fake)
            throw new ArgumentException(options.Provider == LlmProvider.AzureOpenAI ? "Enter the deployment name." : "Enter a model.");
        if (LlmDefaults.RequiresKey(options.Provider) && string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("Add an API key in Settings");

        Uri? endpoint = null;
        if (LlmDefaults.RequiresEndpoint(options.Provider))
        {
            if (!Uri.TryCreate(options.Endpoint?.Trim(), UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException("Enter a valid endpoint URL.");
        }

        var key = options.ApiKey?.Trim() ?? "";
        switch (options.Provider)
        {
            case LlmProvider.Anthropic:
                return new AnthropicChatClient(http, key, model!, browserAccess: isBrowser);

            case LlmProvider.OpenAI:
                return new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) })
                    .GetChatClient(model).AsIChatClient();

            case LlmProvider.AzureOpenAI:
                return new AzureOpenAIClient(endpoint!, new ApiKeyCredential(key), new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) })
                    .GetChatClient(model).AsIChatClient();

            case LlmProvider.Ollama:
                return new OllamaApiClient(new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(5) }, model!);

#if DEBUG
            case LlmProvider.Fake:
                return new FakeChatClient();
#endif
            default:
                throw new ArgumentException($"The provider {options.Provider} isn't available in this build.");
        }
    }
}
