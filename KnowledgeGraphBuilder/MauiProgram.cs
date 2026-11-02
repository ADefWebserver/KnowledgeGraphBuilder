using KnowledgeGraphBuilder.Services;
using KnowledgeGraphBuilder.Shared.Hosting;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.Extensions.Logging;
using Radzen;

namespace KnowledgeGraphBuilder;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

#if WINDOWS
		Platforms.Windows.WindowsShell.Configure(builder);
#endif

		builder.Services.AddMauiBlazorWebView();

		// Pages live as .md files under Documents\KnowledgeGraphBuilder (D16).
		var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KnowledgeGraphBuilder");
		var options = new KnowledgeStoreOptions(root);

		builder.Services.AddRadzenComponents();
		builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
		builder.Services.AddSingleton<IFileDeleter, RecycleBinFileDeleter>();
		builder.Services.AddSingleton<ISettingsStore, MauiSettingsStore>();
		builder.Services.AddSingleton<IHostCapabilities, MauiHostCapabilities>();
		builder.Services.AddKnowledgeGraphShared(options);

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		var store = app.Services.GetRequiredService<FileSystemKnowledgeStore>();
		// Run on the thread pool: blocking the UI thread on async file IO would deadlock.
		Task.Run(async () =>
		{
			store.RecoverInterruptedRenames();
			await KnowledgeSeeder.SeedIfEmptyAsync(store);
		}).GetAwaiter().GetResult();

		return app;
	}
}
