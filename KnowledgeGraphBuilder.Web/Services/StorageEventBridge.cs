using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.JSInterop;

namespace KnowledgeGraphBuilder.Web.Services;

/// <summary>Forwards localStorage changes made in other tabs to the store and the settings (D18).</summary>
public sealed class StorageEventBridge(IJSRuntime js, PersistingKnowledgeStore store, AppSettingsService settings, ILogger<StorageEventBridge> log) : IDisposable
{
    DotNetObjectReference<StorageEventBridge>? self;

    public async Task StartAsync()
    {
        self = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("kgb.registerStorageListener", self, KnowledgeStorageConstants.KeyPrefix);
    }

    [JSInvokable]
    public async Task OnStorageChanged(string? key)
    {
        try
        {
            if (key is null || key == KnowledgeStorageConstants.SettingsKey)
                await settings.ReloadAsync();
            if (key != KnowledgeStorageConstants.SettingsKey)
                await store.OnExternalKeyChangedAsync(key);
        }
        catch (Exception ex)
        {
            log.LogWarning("Couldn't apply a change from another tab: {Message}", ex.Message);
        }
    }

    public void Dispose() => self?.Dispose();
}
