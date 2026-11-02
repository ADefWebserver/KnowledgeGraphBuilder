using System.Net;
using System.Text.Json;
using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using KnowledgeGraphBuilder.Shared.Llm;
using Microsoft.Extensions.AI;

namespace KnowledgeGraphBuilder.Shared.Settings;

public sealed class AppSettingsService(ISettingsStore store, IHostCapabilities host, HttpClient http) : IAppSettingsService, IChatClientProvider
{
    readonly SemaphoreSlim _gate = new(1, 1);
    StoredSettings? _cached;
    IChatClient? _client;

    public event Action? Changed;

    async Task<StoredSettings> LoadAsync()
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync();
        try { return _cached ??= await store.LoadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task<AppSettings> GetAsync()
    {
        var s = await LoadAsync();
        return new AppSettings(host.RootDisplay, s.ProviderValue, s.Model, s.Endpoint, !string.IsNullOrWhiteSpace(s.ApiKey));
    }

    public async Task SaveAsync(LlmProvider provider, string model, string? endpoint, string? newApiKey)
    {
        model = (model ?? "").Trim();
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        if (model.Length == 0 && provider != LlmProvider.Fake) throw new ArgumentException("Enter a model.");
        if (LlmDefaults.RequiresEndpoint(provider) && !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new ArgumentException("Enter a valid endpoint URL.");

        var current = await LoadAsync();
        var key = string.IsNullOrWhiteSpace(newApiKey) ? current.ApiKey : newApiKey.Trim();
        var next = new StoredSettings(provider.ToString(), model, LlmDefaults.RequiresEndpoint(provider) ? endpoint : null, key);
        await store.SaveAsync(next);
        Update(next);
    }

    public async Task ClearApiKeyAsync()
    {
        await store.ClearApiKeyAsync();
        var current = await LoadAsync();
        Update(current with { ApiKey = null });
    }

    /// <summary>Called when another tab changed the settings key.</summary>
    public async Task ReloadAsync()
    {
        var fresh = await store.LoadAsync();
        Update(fresh);
    }

    void Update(StoredSettings next)
    {
        _cached = next;
        var old = _client;
        _client = null;
        old?.Dispose();
        Changed?.Invoke();
    }

    public async Task<ChatClientResult> GetClientAsync()
    {
        var s = await LoadAsync();
        if (_client is not null) return new ChatClientResult(_client, null);
        try
        {
            _client = ChatClientFactory.Create(new LlmOptions(s.ProviderValue, s.Model, s.Endpoint, s.ApiKey), http, host.IsBrowser);
            return new ChatClientResult(_client, null);
        }
        catch (ArgumentException ex)
        {
            return new ChatClientResult(null, ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "Add an API key in Settings" : ex.Message);
        }
    }

    public async Task<string?> TestConnectionAsync(CancellationToken ct)
    {
        var result = await GetClientAsync();
        if (!result.Ok) return result.Reason;
        try
        {
            var response = await result.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with OK.")],
                new ChatOptions { MaxOutputTokens = 1 }, ct);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The request timed out.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var s = await LoadAsync();
            return DescribeError(ex, LlmDefaults.HostFor(s.ProviderValue, s.Endpoint), host.IsBrowser);
        }
    }

    /// <summary>A user-facing message for a provider failure. Never contains the API key.</summary>
    public static string DescribeError(Exception ex, string? providerHost, bool isBrowser)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return "The provider rejected the API key (" + (int)http.StatusCode + "). Check the key in Settings.";
            if (http.StatusCode is null && isBrowser)
                return $"The browser blocked the request to {providerHost ?? "the provider"}. See Settings help.";
            return Redact(http.Message);
        }
        if (ex is System.ClientModel.ClientResultException cre)
        {
            if (cre.Status is 401 or 403) return $"The provider rejected the API key ({cre.Status}). Check the key in Settings.";
            if (cre.Status == 0 && isBrowser) return $"The browser blocked the request to {providerHost ?? "the provider"}. See Settings help.";
            return Redact(cre.Message);
        }
        if (ex is JsonException) return "The provider returned an unexpected response.";
        return Redact(ex.Message);
    }

    static string Redact(string message)
    {
        // Defensive: strip anything that looks like a key.
        message = System.Text.RegularExpressions.Regex.Replace(message, @"(sk-[A-Za-z0-9_\-]{8,}|[A-Fa-f0-9]{32,})", "[redacted]");
        return message.Length > 400 ? message[..400] + "…" : message;
    }
}

/// <summary>WASM settings: one localStorage key, <c>kgb:v1:settings</c>, in plaintext (D20).</summary>
public sealed class BrowserSettingsStore(IKeyValueStore kv) : ISettingsStore
{
    public async Task<StoredSettings> LoadAsync()
    {
        var json = await kv.GetAsync(KnowledgeStorageConstants.SettingsKey);
        if (string.IsNullOrWhiteSpace(json)) return StoredSettings.Default;
        try
        {
            var s = JsonSerializer.Deserialize<StoredSettings>(json, KnowledgeStorageConstants.Json);
            return s is null || string.IsNullOrWhiteSpace(s.Provider) ? StoredSettings.Default : s with { Model = s.Model ?? "" };
        }
        catch (JsonException) { return StoredSettings.Default; }
    }

    public Task SaveAsync(StoredSettings settings) =>
        kv.SetAsync(KnowledgeStorageConstants.SettingsKey, JsonSerializer.Serialize(settings, KnowledgeStorageConstants.Json));

    public async Task ClearApiKeyAsync()
    {
        var current = await LoadAsync();
        await SaveAsync(current with { ApiKey = null });
    }
}
