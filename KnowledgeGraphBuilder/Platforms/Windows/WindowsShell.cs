using KnowledgeGraphBuilder.Shared.Services;
using Microsoft.Maui.LifecycleEvents;

namespace KnowledgeGraphBuilder.Platforms.Windows;

/// <summary>Asks before closing the window while pages have unsaved edits.</summary>
public static class WindowsShell
{
	public static void Configure(MauiAppBuilder builder)
	{
		builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
		{
			var allowClose = false;
			window.AppWindow.Closing += async (sender, args) =>
			{
				if (allowClose) return;
				var state = IPlatformApplication.Current?.Services.GetService<WorkspaceState>();
				if (state is null || !state.HasUnsaved) return;

				args.Cancel = true;
				var page = Application.Current?.Windows.FirstOrDefault()?.Page;
				if (page is null) return;
				var pages = string.Join("\n", state.UnsavedPaths.Take(10));
				var close = await page.DisplayAlertAsync("Unsaved changes",
					$"These pages have unsaved changes:\n{pages}\n\nClose without saving?", "Close without saving", "Cancel");
				if (!close) return;
				allowClose = true;
				sender.Destroy();
				Application.Current?.Quit();
			};
		})));
	}
}
