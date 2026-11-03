using GuideFlow.Services;
using KnowledgeGraphBuilder.Shared.Chat;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Services;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KnowledgeGraphBuilder.Shared.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared services. The host registers <see cref="IHostCapabilities"/>, <see cref="ISettingsStore"/>,
    /// an <see cref="HttpClient"/>, and optionally its own <see cref="IFileDeleter"/> and <see cref="IKnowledgeStore"/> first.
    /// Every service is a singleton: one per MAUI window or browser tab.
    /// </summary>
    public static IServiceCollection AddKnowledgeGraphShared(this IServiceCollection services, KnowledgeStoreOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton<IFileDeleter, PermanentFileDeleter>();
        services.AddSingleton(sp => new FileSystemKnowledgeStore(options, sp.GetRequiredService<IFileDeleter>()));
        services.TryAddSingleton<IKnowledgeStore>(sp => sp.GetRequiredService<FileSystemKnowledgeStore>());
        services.AddSingleton<KnowledgeGraphService>();
        services.AddSingleton<IKnowledgeGraphIndex>(sp => sp.GetRequiredService<KnowledgeGraphService>());
        services.AddSingleton<LocalKnowledgeWorkspace>();
        services.AddSingleton<IKnowledgeWorkspace>(sp => sp.GetRequiredService<LocalKnowledgeWorkspace>());

        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<IAppSettingsService>(sp => sp.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IChatClientProvider>(sp => sp.GetRequiredService<AppSettingsService>());

        services.AddSingleton(new ChatOptionsConfig());
        services.AddSingleton<ChatTurnContextAccessor>();
        services.AddSingleton<KnowledgeTools>();
        services.AddSingleton<ChatEngine>();
        services.AddSingleton<IChatOrchestrator>(sp => sp.GetRequiredService<ChatEngine>());

        services.AddSingleton<WorkspaceState>();
        services.AddSingleton<KnowledgeGraphBuilder.Shared.Components.Chat.ChatTranscript>();
        // Scoped: in MAUI, IJSRuntime is scoped to the BlazorWebView, so a singleton would capture the root one and fail.
        services.AddScoped<BrowserInterop>();
        services.AddGuideFlow();
        return services;
    }
}
