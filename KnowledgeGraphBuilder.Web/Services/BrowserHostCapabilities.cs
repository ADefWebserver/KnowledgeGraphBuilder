using KnowledgeGraphBuilder.Shared.Settings;

namespace KnowledgeGraphBuilder.Web.Services;

public sealed class BrowserHostCapabilities(string rootPath) : IHostCapabilities
{
    public bool CanToggleTheme => true;
    public bool CanOpenFolder => false;
    public bool IsBrowser => true;
    public bool HasBackup => true;
    public bool DeletesAreRecoverable => false;
    public string RootDisplay => "Browser storage";
    public string RootPath => rootPath;
    public void OpenRootFolder() { }
}
