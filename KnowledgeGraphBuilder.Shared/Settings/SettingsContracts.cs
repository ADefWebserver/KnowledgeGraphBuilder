using System.Diagnostics.CodeAnalysis;
using KnowledgeGraphBuilder.Shared.Llm;
using Microsoft.Extensions.AI;

namespace KnowledgeGraphBuilder.Shared.Settings;

public sealed record StoredSettings(string Provider, string Model, string? Endpoint, string? ApiKey)
{
    public static StoredSettings Default => new(LlmDefaults.Provider.ToString(), LlmDefaults.AnthropicModel, null, null);

    public LlmProvider ProviderValue =>
        Enum.TryParse<LlmProvider>(Provider, ignoreCase: true, out var p) && LlmDefaults.Available().Contains(p) ? p : LlmDefaults.Provider;
}

/// <summary>Host-specific storage: Preferences + SecureStorage on MAUI, localStorage in the browser.</summary>
public interface ISettingsStore
{
    Task<StoredSettings> LoadAsync();
    Task SaveAsync(StoredSettings settings);
    Task ClearApiKeyAsync();
}

public sealed record AppSettings(string RootDisplay, LlmProvider Provider, string Model, string? Endpoint, bool ApiKeyConfigured);

public interface IAppSettingsService
{
    Task<AppSettings> GetAsync();

    /// <param name="newApiKey">null keeps the current key.</param>
    Task SaveAsync(LlmProvider provider, string model, string? endpoint, string? newApiKey);
    Task ClearApiKeyAsync();

    /// <returns>null when the connection works, otherwise an error message.</returns>
    Task<string?> TestConnectionAsync(CancellationToken ct);

    /// <summary>
    /// Lists the provider's models for the form's current values. <paramref name="newApiKey"/> null uses the saved key.
    /// Successful lists are cached for a day; <paramref name="refresh"/> skips the cache.
    /// </summary>
    Task<ModelListResult> ListModelsAsync(LlmProvider provider, string? endpoint, string? newApiKey, bool refresh, CancellationToken ct);

    event Action? Changed;
}

public interface IHostCapabilities
{
    bool CanToggleTheme { get; }
    bool CanOpenFolder { get; }
    bool IsBrowser { get; }
    bool HasBackup { get; }
    bool DeletesAreRecoverable { get; }
    string RootDisplay { get; }
    string RootPath { get; }
    void OpenRootFolder();
}

public readonly record struct ChatClientResult(IChatClient? Client, string? Reason)
{
    [MemberNotNullWhen(true, nameof(Client))]
    public bool Ok => Client is not null;
}

public interface IChatClientProvider
{
    /// <summary>Returns the client for the current settings, or a reason such as "Add an API key in Settings".</summary>
    Task<ChatClientResult> GetClientAsync();
}
