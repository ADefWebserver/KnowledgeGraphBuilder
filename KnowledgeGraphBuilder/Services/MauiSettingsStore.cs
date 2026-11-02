using KnowledgeGraphBuilder.Shared.Settings;

namespace KnowledgeGraphBuilder.Services;

/// <summary>Provider, model and endpoint in Preferences; the API key in SecureStorage (DPAPI on Windows).</summary>
public sealed class MauiSettingsStore : ISettingsStore
{
    const string ProviderKey = "kgb.llm.provider";
    const string ModelKey = "kgb.llm.model";
    const string EndpointKey = "kgb.llm.endpoint";
    const string ApiKeyKey = "kgb.llm.apiKey";

    public async Task<StoredSettings> LoadAsync()
    {
        var defaults = StoredSettings.Default;
        string? key = null;
        try { key = await SecureStorage.Default.GetAsync(ApiKeyKey); }
        catch (Exception) { /* Secure storage can be unavailable; treat as no key. */ }

        var endpoint = Preferences.Default.Get<string?>(EndpointKey, null);
        return new StoredSettings(
            Preferences.Default.Get(ProviderKey, defaults.Provider),
            Preferences.Default.Get(ModelKey, defaults.Model),
            string.IsNullOrEmpty(endpoint) ? null : endpoint,
            string.IsNullOrEmpty(key) ? null : key);
    }

    public async Task SaveAsync(StoredSettings settings)
    {
        Preferences.Default.Set(ProviderKey, settings.Provider);
        Preferences.Default.Set(ModelKey, settings.Model);
        Preferences.Default.Set(EndpointKey, settings.Endpoint ?? "");
        if (string.IsNullOrEmpty(settings.ApiKey)) SecureStorage.Default.Remove(ApiKeyKey);
        else await SecureStorage.Default.SetAsync(ApiKeyKey, settings.ApiKey);
    }

    public Task ClearApiKeyAsync()
    {
        SecureStorage.Default.Remove(ApiKeyKey);
        return Task.CompletedTask;
    }
}
