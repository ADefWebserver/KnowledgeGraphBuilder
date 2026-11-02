using System.Diagnostics;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Settings;

namespace KnowledgeGraphBuilder.Services;

public sealed class MauiHostCapabilities(KnowledgeStoreOptions options, IFileDeleter deleter) : IHostCapabilities
{
    public bool CanToggleTheme => true;
    public bool CanOpenFolder => OperatingSystem.IsWindows();
    public bool IsBrowser => false;
    public bool HasBackup => false;
    public bool DeletesAreRecoverable => deleter.IsRecoverable;
    public string RootDisplay => Path.GetFileName(options.RootPath);
    public string RootPath => options.RootPath;

    public void OpenRootFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{options.RootPath}\"") { UseShellExecute = true });
    }
}
