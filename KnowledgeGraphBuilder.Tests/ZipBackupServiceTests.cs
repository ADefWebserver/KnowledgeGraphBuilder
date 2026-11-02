using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeGraphBuilder.Tests;

public sealed class ZipBackupServiceTests : IDisposable
{
    readonly WorkspaceFixture _source = new();
    readonly WorkspaceFixture _target = new(seed: false);

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    static ZipBackupService Backup(IKnowledgeWorkspace workspace) => new(workspace, new ThrowingJsRuntime());

    async Task<MemoryStream> ExportAsync(IKnowledgeWorkspace workspace)
    {
        var buffer = new MemoryStream();
        await Backup(workspace).WriteExportAsync(buffer);
        buffer.Position = 0;
        return buffer;
    }

    static MemoryStream Zip(params (string Name, string? Text)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (text is null) continue;
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    static string Manifest(int format) => $$"""{"app":"KnowledgeGraphBuilder","format":{{format}},"exported":"2026-11-01T00:00:00Z"}""";

    [Fact]
    public async Task Export_ContainsManifestFoldersAndPagesOnly()
    {
        await _source.Workspace.CreateFolderAsync("Empty");
        using var buffer = await ExportAsync(_source.Workspace);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(ZipBackupService.ManifestName, names[0]);
        Assert.Contains("Empty/", names);
        Assert.Contains("Getting Started/", names);
        Assert.Contains("Welcome.md", names);
        Assert.Contains("Getting Started/Install.md", names);
        Assert.All(names.Skip(1), n => Assert.True(n.EndsWith('/') || n.EndsWith(".md"), n));
        Assert.DoesNotContain(names, n => n.Contains("settings", StringComparison.OrdinalIgnoreCase));

        using var reader = new StreamReader(zip.GetEntry(ZipBackupService.ManifestName)!.Open());
        var manifest = JsonNode.Parse(reader.ReadToEnd())!;
        Assert.Equal("KnowledgeGraphBuilder", (string?)manifest["app"]);
        Assert.Equal(1, (int)manifest["format"]!);
        Assert.True(DateTimeOffset.TryParse((string?)manifest["exported"], out _));
    }

    [Fact]
    public async Task Export_NeverIncludesSettings_FromBrowserStorage()
    {
        var kv = new InMemoryKeyValueStore();
        await kv.SetAsync(KnowledgeStorageConstants.SettingsKey, """{"provider":"Anthropic","apiKey":"sk-ant-secret-key"}""");
        using var dir = new TempDir();
        var store = new PersistingKnowledgeStore(new FileSystemKnowledgeStore(new KnowledgeStoreOptions(dir.Path), new PermanentFileDeleter()),
            kv, NullLogger<PersistingKnowledgeStore>.Instance);
        await store.HydrateAsync();
        await KnowledgeSeeder.SeedIfEmptyAsync(store);
        using var workspace = new LocalKnowledgeWorkspace(store, new KnowledgeGraphService(store), NullLogger<LocalKnowledgeWorkspace>.Instance);

        using var buffer = await ExportAsync(workspace);

        var bytes = buffer.ToArray();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("sk-ant-secret-key", reader.ReadToEnd());
        }
    }

    [Fact]
    public async Task RoundTrip_RestoresEveryPageAndFolder()
    {
        await _source.Workspace.CreateFolderAsync("Empty/Nested");
        await _source.Workspace.SavePageAsync("Ünïcødé/页面.md", "# 页面\n", 0);
        using var buffer = await ExportAsync(_source.Workspace);

        var result = await Backup(_target.Workspace).ImportAsync(buffer, ImportMode.Merge);

        var sourcePages = (await _source.Workspace.GetAllPagePathsAsync()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(sourcePages.Count, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Rejected);
        Assert.Equal(sourcePages, (await _target.Workspace.GetAllPagePathsAsync()).Order(StringComparer.Ordinal));
        foreach (var path in sourcePages)
            Assert.Equal((await _source.Workspace.ReadPageAsync(path)).Markdown, (await _target.Workspace.ReadPageAsync(path)).Markdown);
        Assert.True(await _target.Workspace.ExistsAsync("Empty/Nested"));
    }

    [Fact]
    public async Task Merge_KeepsExistingPages_AndSkipsDuplicates()
    {
        await _target.Workspace.SavePageAsync("Welcome.md", "# My own welcome\n", 0);
        await _target.Workspace.SavePageAsync("Mine.md", "# Mine\n", 0);
        using var buffer = await ExportAsync(_source.Workspace);
        var total = (await _source.Workspace.GetAllPagePathsAsync()).Count;

        var result = await Backup(_target.Workspace).ImportAsync(buffer, ImportMode.Merge);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(total - 1, result.Imported);
        Assert.Equal("# My own welcome\n", (await _target.Workspace.ReadPageAsync("Welcome.md")).Markdown);
        Assert.True(await _target.Workspace.ExistsAsync("Mine.md"));
    }

    [Fact]
    public async Task Replace_RemovesEverythingFirst()
    {
        await _target.Workspace.SavePageAsync("Welcome.md", "# My own welcome\n", 0);
        await _target.Workspace.SavePageAsync("Old/Mine.md", "# Mine\n", 0);
        using var buffer = await ExportAsync(_source.Workspace);

        var result = await Backup(_target.Workspace).ImportAsync(buffer, ImportMode.Replace);

        Assert.Equal(0, result.Skipped);
        Assert.False(await _target.Workspace.ExistsAsync("Old"));
        Assert.Equal((await _source.Workspace.ReadPageAsync("Welcome.md")).Markdown, (await _target.Workspace.ReadPageAsync("Welcome.md")).Markdown);
    }

    [Fact]
    public async Task Import_RejectsTraversalAndNonMarkdownEntries()
    {
        using var zip = Zip(
            (ZipBackupService.ManifestName, Manifest(1)),
            ("ok.md", "# ok"),
            ("Folder/nested.md", "# nested"),
            ("../evil.md", "evil"),
            ("a/../../evil2.md", "evil"),
            ("/abs.md", "evil"),
            ("C:/drive.md", "evil"),
            ("..\\back.md", "evil"),
            ("notes.txt", "text"),
            ("script.js", "alert(1)"),
            (".hidden/x.md", "hidden"),
            ("CON.md", "reserved"));

        var result = await Backup(_target.Workspace).ImportAsync(zip, ImportMode.Merge);

        Assert.Equal(2, result.Imported);
        Assert.Equal(9, result.Rejected.Count);
        Assert.Contains("../evil.md", result.Rejected);
        Assert.Contains("notes.txt", result.Rejected);
        Assert.Equal(["Folder/nested.md", "ok.md"], (await _target.Workspace.GetAllPagePathsAsync()).Order(StringComparer.Ordinal));

        var parent = Path.GetDirectoryName(_target.Dir.Path)!;
        Assert.False(File.Exists(Path.Combine(parent, "evil.md")));
        Assert.False(File.Exists(Path.Combine(parent, "evil2.md")));
        Assert.False(File.Exists(Path.Combine(parent, "back.md")));
    }

    [Fact]
    public async Task Import_RejectsANewerFormat()
    {
        using var zip = Zip((ZipBackupService.ManifestName, Manifest(2)), ("ok.md", "# ok"));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => Backup(_target.Workspace).ImportAsync(zip, ImportMode.Replace));

        Assert.Contains("format 2", ex.Message);
        Assert.Empty(await _target.Workspace.GetAllPagePathsAsync());
    }

    [Fact]
    public async Task Import_RejectsAnInvalidZip()
    {
        using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip"));
        await Assert.ThrowsAsync<KnowledgeException>(() => Backup(_target.Workspace).ImportAsync(notZip, ImportMode.Merge));
    }

    [Fact]
    public async Task Import_RejectsAnUnreadableManifest()
    {
        using var zip = Zip((ZipBackupService.ManifestName, "{ not json"), ("ok.md", "# ok"));
        await Assert.ThrowsAsync<KnowledgeException>(() => Backup(_target.Workspace).ImportAsync(zip, ImportMode.Merge));
    }

    [Fact]
    public async Task Import_RejectsOversizedBackups()
    {
        using var big = new MemoryStream(new byte[KnowledgeStorageConstants.MaxImportBytes + 1]);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => Backup(_target.Workspace).ImportAsync(big, ImportMode.Merge));
        Assert.Contains("10 MB", ex.Message);
    }
}
