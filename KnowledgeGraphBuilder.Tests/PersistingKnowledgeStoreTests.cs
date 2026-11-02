using System.Text.Json;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Knowledge.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using static KnowledgeGraphBuilder.Shared.Knowledge.Persistence.KnowledgeStorageConstants;

namespace KnowledgeGraphBuilder.Tests;

public sealed class PersistingKnowledgeStoreTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly InMemoryKeyValueStore _kv = new();

    public void Dispose() => _dir.Dispose();

    PersistingKnowledgeStore CreateStore(TempDir? dir = null, IKeyValueStore? kv = null) =>
        new(new FileSystemKnowledgeStore(new KnowledgeStoreOptions((dir ?? _dir).Path), new PermanentFileDeleter()),
            kv ?? _kv, NullLogger<PersistingKnowledgeStore>.Instance);

    async Task<PersistingKnowledgeStore> HydratedAsync()
    {
        var store = CreateStore();
        await store.HydrateAsync();
        return store;
    }

    StorageIndex ReadIndex() => JsonSerializer.Deserialize<StorageIndex>(_kv.Items[IndexKey], Json)!;

    async Task WriteIndexAsync(StorageIndex index) => await _kv.SetAsync(IndexKey, JsonSerializer.Serialize(index, Json));

    [Fact]
    public async Task Hydrate_RestoresPagesFoldersAndTicks()
    {
        var ticks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
        await _kv.SetAsync(PageKey("Welcome.md"), "# Welcome\n");
        await _kv.SetAsync(PageKey("Getting Started/Install.md"), "# Install\n");
        await WriteIndexAsync(new StorageIndex(1, ["Getting Started", "Empty"],
            new Dictionary<string, long> { ["Welcome.md"] = ticks, ["Getting Started/Install.md"] = ticks + 10 }));
        _kv.WriteLog.Clear();

        var store = await HydratedAsync();

        var welcome = await store.ReadPageAsync("Welcome.md");
        Assert.Equal("# Welcome\n", welcome.Markdown);
        Assert.Equal(ticks, welcome.ModifiedTicks);
        Assert.Equal(ticks + 10, (await store.ReadPageAsync("Getting Started/Install.md")).ModifiedTicks);
        Assert.Equal(["Empty", "Getting Started", "Welcome.md"], (await store.GetChildrenAsync("")).Select(c => c.Name));
        Assert.Equal(ticks, (await store.GetChildrenAsync("")).Single(c => c.Name == "Welcome.md").ModifiedTicks);
        Assert.Empty(_kv.WriteLog); // nothing to repair
    }

    [Fact]
    public async Task Hydrate_DropsIndexEntriesWithoutAKey()
    {
        await _kv.SetAsync(PageKey("a.md"), "a");
        await WriteIndexAsync(new StorageIndex(1, [], new Dictionary<string, long> { ["a.md"] = 100, ["missing.md"] = 200 }));

        var store = await HydratedAsync();

        Assert.Equal(["a.md"], await store.GetAllPagePathsAsync());
        Assert.Equal(["a.md"], ReadIndex().Pages.Keys);
        // Ticks that aren't valid file times (here 100) still come from the index.
        Assert.Equal(100, (await store.ReadPageAsync("a.md")).ModifiedTicks);
    }

    [Fact]
    public async Task Hydrate_AdoptsOrphanKeys()
    {
        await _kv.SetAsync(PageKey("a.md"), "a");
        await _kv.SetAsync(PageKey("Folder/orphan.md"), "orphan");
        await _kv.SetAsync(PageKey("../evil.md"), "evil");
        await WriteIndexAsync(new StorageIndex(1, [], new Dictionary<string, long> { ["a.md"] = 100 }));

        var store = await HydratedAsync();

        Assert.Equal("orphan", (await store.ReadPageAsync("Folder/orphan.md")).Markdown);
        var index = ReadIndex();
        Assert.Contains("Folder/orphan.md", index.Pages.Keys);
        Assert.Contains("Folder", index.Folders);
        Assert.DoesNotContain(index.Pages.Keys, k => k.Contains(".."));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir.Path)!, "evil.md")));
    }

    [Fact]
    public async Task Hydrate_WithoutAnIndex_StartsEmpty_AndSeedIsPersisted()
    {
        var store = await HydratedAsync();
        Assert.Empty(await store.GetAllPagePathsAsync());

        Assert.True(await KnowledgeSeeder.SeedIfEmptyAsync(store));

        var seeded = KnowledgeSeeder.GetSeedPages().Select(p => p.Path).ToList();
        Assert.Contains("Welcome.md", seeded);
        foreach (var path in seeded) Assert.True(_kv.Items.ContainsKey(PageKey(path)), path);
        Assert.Equal(seeded.Order(StringComparer.Ordinal), ReadIndex().Pages.Keys.Order(StringComparer.Ordinal));

        // A second hydration from the same storage gives the same pages and ticks.
        var other = new TempDir();
        try
        {
            var again = CreateStore(other);
            await again.HydrateAsync();
            foreach (var path in seeded)
                Assert.Equal((await store.ReadPageAsync(path)).ModifiedTicks, (await again.ReadPageAsync(path)).ModifiedTicks);
            Assert.False(await KnowledgeSeeder.SeedIfEmptyAsync(again));
        }
        finally { other.Dispose(); }
    }

    [Fact]
    public async Task Save_WritesPageKeyThenIndex()
    {
        var store = await HydratedAsync();
        _kv.WriteLog.Clear();

        var result = await store.SavePageAsync("F/a.md", "# A", 0);

        Assert.Equal(SaveStatus.Created, result.Status);
        Assert.Equal(["set " + PageKey("F/a.md"), "set " + IndexKey], _kv.WriteLog);
        Assert.Equal("# A", _kv.Items[PageKey("F/a.md")]);
        var index = ReadIndex();
        Assert.Equal(result.ModifiedTicks, index.Pages["F/a.md"]);
        Assert.Contains("F", index.Folders);
    }

    [Fact]
    public async Task Save_TicksAreMonotonic_AndCheckedAgainstTheIndex()
    {
        var store = await HydratedAsync();
        var first = await store.SavePageAsync("a.md", "1", 0);
        var second = await store.SavePageAsync("a.md", "2", first.ModifiedTicks);
        Assert.Equal(SaveStatus.Saved, second.Status);
        Assert.True(second.ModifiedTicks > first.ModifiedTicks);
        Assert.Equal(second.ModifiedTicks, (await store.ReadPageAsync("a.md")).ModifiedTicks);

        Assert.Equal(SaveStatus.Conflict, (await store.SavePageAsync("a.md", "3", first.ModifiedTicks)).Status);
        Assert.Equal(SaveStatus.AlreadyExists, (await store.SavePageAsync("a.md", "3", 0)).Status);
        Assert.Equal("2", _kv.Items[PageKey("a.md")]);
    }

    [Fact]
    public async Task CreateFolder_WritesOnlyTheIndex()
    {
        var store = await HydratedAsync();
        _kv.WriteLog.Clear();

        await store.CreateFolderAsync("Empty/Nested");

        Assert.Equal(["set " + IndexKey], _kv.WriteLog);
        Assert.Contains("Empty", ReadIndex().Folders);
        Assert.Contains("Empty/Nested", ReadIndex().Folders);
    }

    [Fact]
    public async Task RenamePage_SetsNewKey_ThenIndex_ThenRemovesOldKey()
    {
        var store = await HydratedAsync();
        var saved = await store.SavePageAsync("Old.md", "text", 0);
        _kv.WriteLog.Clear();

        Assert.Equal("New.md", await store.RenameAsync("Old.md", "New"));

        Assert.Equal(["set " + PageKey("New.md"), "set " + IndexKey, "remove " + PageKey("Old.md")], _kv.WriteLog);
        Assert.Equal("text", _kv.Items[PageKey("New.md")]);
        Assert.False(_kv.Items.ContainsKey(PageKey("Old.md")));
        var index = ReadIndex();
        Assert.Equal(saved.ModifiedTicks, index.Pages["New.md"]);
        Assert.False(index.Pages.ContainsKey("Old.md"));
    }

    [Fact]
    public async Task MovePage_IntoFolder()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("a.md", "text", 0);
        await store.CreateFolderAsync("T");

        Assert.Equal("T/a.md", await store.MoveAsync("a.md", "T"));

        Assert.Equal("text", _kv.Items[PageKey("T/a.md")]);
        Assert.False(_kv.Items.ContainsKey(PageKey("a.md")));
        Assert.Equal("text", (await store.ReadPageAsync("T/a.md")).Markdown);
    }

    [Fact]
    public async Task RenameFolder_MovesEveryKey_AndWritesTheIndexOnce()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("Proj/a.md", "a", 0);
        await store.SavePageAsync("Proj/Sub/b.md", "b", 0);
        await store.SavePageAsync("Other.md", "o", 0);
        _kv.WriteLog.Clear();

        Assert.Equal("Work", await store.RenameAsync("Proj", "Work"));

        Assert.Single(_kv.WriteLog, w => w == "set " + IndexKey);
        var indexWrite = _kv.WriteLog.IndexOf("set " + IndexKey);
        Assert.All(_kv.WriteLog.Take(indexWrite), w => Assert.StartsWith("set " + PageKey("Work/"), w));
        Assert.All(_kv.WriteLog.Skip(indexWrite + 1), w => Assert.StartsWith("remove " + PageKey("Proj/"), w));
        Assert.Equal(2, _kv.WriteLog.Count(w => w.StartsWith("remove ")));

        Assert.Equal("a", _kv.Items[PageKey("Work/a.md")]);
        Assert.Equal("b", _kv.Items[PageKey("Work/Sub/b.md")]);
        Assert.DoesNotContain(_kv.Items.Keys, k => k.StartsWith(PageKey("Proj/")));
        var index = ReadIndex();
        Assert.Contains("Work", index.Folders);
        Assert.Contains("Work/Sub", index.Folders);
        Assert.DoesNotContain(index.Folders, f => f.StartsWith("Proj"));
        Assert.Equal(["Other.md", "Work/Sub/b.md", "Work/a.md"], index.Pages.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Delete_WritesIndexThenRemovesKeys()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("F/a.md", "a", 0);
        await store.SavePageAsync("F/b.md", "b", 0);
        await store.SavePageAsync("keep.md", "k", 0);
        _kv.WriteLog.Clear();

        await store.DeleteAsync("F");

        Assert.Equal("set " + IndexKey, _kv.WriteLog[0]);
        Assert.Equal(["remove " + PageKey("F/a.md"), "remove " + PageKey("F/b.md")], _kv.WriteLog.Skip(1).Order(StringComparer.Ordinal));
        Assert.Equal(["keep.md"], ReadIndex().Pages.Keys);
        Assert.DoesNotContain("F", ReadIndex().Folders);
        Assert.Equal(["keep.md"], await store.GetAllPagePathsAsync());
    }

    [Fact]
    public async Task QuotaError_OnUpdate_RollsBackTheInMemoryChange()
    {
        var store = await HydratedAsync();
        var saved = await store.SavePageAsync("a.md", "small", 0);
        _kv.QuotaBytes = 2_000;

        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.SavePageAsync("a.md", new string('x', 2_000), saved.ModifiedTicks));

        var page = await store.ReadPageAsync("a.md");
        Assert.Equal("small", page.Markdown);
        Assert.Equal(saved.ModifiedTicks, page.ModifiedTicks);
        Assert.Equal("small", _kv.Items[PageKey("a.md")]);
        Assert.Equal(saved.ModifiedTicks, ReadIndex().Pages["a.md"]);

        _kv.QuotaBytes = null;
        Assert.Equal(SaveStatus.Saved, (await store.SavePageAsync("a.md", "next", saved.ModifiedTicks)).Status);
    }

    [Fact]
    public async Task QuotaError_OnCreate_RemovesThePageAndNewFolders()
    {
        var store = await HydratedAsync();
        _kv.QuotaBytes = 500;

        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.SavePageAsync("New/big.md", new string('x', 1_000), 0));

        Assert.False(await store.ExistsAsync("New/big.md"));
        Assert.False(await store.ExistsAsync("New"));
        Assert.False(_kv.Items.ContainsKey(PageKey("New/big.md")));
        Assert.Empty(await store.GetAllPagePathsAsync());
    }

    [Fact]
    public async Task QuotaError_WhenTheIndexWriteFails_RemovesThePageKey()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("a.md", "a", 0);
        // Enough for the page key but not for the index that follows it.
        var used = _kv.Items.Sum(kv => (long)(kv.Key.Length + kv.Value.Length) * 2);
        var page = new string('y', 100);
        _kv.QuotaBytes = used + (PageKey("b.md").Length + page.Length) * 2 + 10;

        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.SavePageAsync("b.md", page, 0));

        Assert.False(_kv.Items.ContainsKey(PageKey("b.md")));
        Assert.False(await store.ExistsAsync("b.md"));
        Assert.DoesNotContain("b.md", ReadIndex().Pages.Keys);
    }

    [Fact]
    public async Task QuotaError_OnRename_UndoesTheMove()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("Old.md", new string('x', 400), 0);
        _kv.QuotaBytes = _kv.Items.Sum(kv => (long)(kv.Key.Length + kv.Value.Length) * 2) + 100;

        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.RenameAsync("Old.md", "New"));

        Assert.True(await store.ExistsAsync("Old.md"));
        Assert.False(await store.ExistsAsync("New.md"));
        Assert.True(_kv.Items.ContainsKey(PageKey("Old.md")));
        Assert.False(_kv.Items.ContainsKey(PageKey("New.md")));
        Assert.Contains("Old.md", ReadIndex().Pages.Keys);
    }

    [Fact]
    public async Task ExternalPageChange_ReloadsThatPage()
    {
        var store = await HydratedAsync();
        var saved = await store.SavePageAsync("a.md", "mine", 0);
        var changes = new List<KnowledgeChange>();
        store.ExternalChange += changes.Add;

        // Another tab writes the page key and the index.
        var index = ReadIndex();
        index.Pages["a.md"] = saved.ModifiedTicks + 5_000;
        await _kv.SetAsync(PageKey("a.md"), "theirs");
        await WriteIndexAsync(index);
        await store.OnExternalKeyChangedAsync(PageKey("a.md"));

        Assert.Equal([new KnowledgeChange(ChangeKind.Updated, "a.md")], changes);
        var page = await store.ReadPageAsync("a.md");
        Assert.Equal("theirs", page.Markdown);
        Assert.Equal(saved.ModifiedTicks + 5_000, page.ModifiedTicks);

        // A save based on the old ticks is now a conflict.
        Assert.Equal(SaveStatus.Conflict, (await store.SavePageAsync("a.md", "stale", saved.ModifiedTicks)).Status);
    }

    [Fact]
    public async Task ExternalChange_NewAndDeletedPages()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("gone.md", "x", 0);
        var changes = new List<KnowledgeChange>();
        store.ExternalChange += changes.Add;

        await _kv.SetAsync(PageKey("F/new.md"), "new");
        await store.OnExternalKeyChangedAsync(PageKey("F/new.md"));
        await _kv.RemoveAsync(PageKey("gone.md"));
        await store.OnExternalKeyChangedAsync(PageKey("gone.md"));

        Assert.Equal([new KnowledgeChange(ChangeKind.Created, "F/new.md"), new KnowledgeChange(ChangeKind.Deleted, "gone.md")], changes);
        Assert.Equal("new", (await store.ReadPageAsync("F/new.md")).Markdown);
        Assert.False(await store.ExistsAsync("gone.md"));
    }

    [Fact]
    public async Task ExternalIndexChange_Reconciles()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("a.md", "a", 0);
        await store.SavePageAsync("b.md", "b", 0);
        var changes = new List<KnowledgeChange>();
        store.ExternalChange += changes.Add;

        var index = ReadIndex();
        index.Pages.Remove("b.md");
        await _kv.RemoveAsync(PageKey("b.md"));
        await WriteIndexAsync(index);
        await store.OnExternalKeyChangedAsync(IndexKey);

        Assert.Equal(ChangeKind.Reloaded, Assert.Single(changes).Kind);
        Assert.Equal(["a.md"], await store.GetAllPagePathsAsync());
    }

    [Fact]
    public async Task NullKey_Rehydrates()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("a.md", "a", 0);
        var changes = new List<KnowledgeChange>();
        store.ExternalChange += changes.Add;

        foreach (var key in (await _kv.KeysAsync(KeyPrefix)).ToList()) await _kv.RemoveAsync(key);
        await store.OnExternalKeyChangedAsync(null);

        Assert.Equal([new KnowledgeChange(ChangeKind.Reloaded, "")], changes);
        Assert.Empty(await store.GetAllPagePathsAsync());
    }

    [Fact]
    public async Task UnrelatedKey_IsIgnored()
    {
        var store = await HydratedAsync();
        var changes = new List<KnowledgeChange>();
        store.ExternalChange += changes.Add;

        await store.OnExternalKeyChangedAsync(SettingsKey);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task Usage_CountsKeysAndValues()
    {
        var store = await HydratedAsync();
        await store.SavePageAsync("a.md", "abc", 0);

        var usage = await store.GetUsageAsync();

        var expected = _kv.Items.Sum(kv => (long)(kv.Key.Length + kv.Value.Length) * 2);
        Assert.Equal(expected, usage.EstimatedBytes);
        Assert.Equal(AssumedQuotaBytes, usage.AssumedQuotaBytes);
        Assert.Equal(1, usage.Pages);
        Assert.False(usage.IsNearlyFull);
    }

    [Fact]
    public async Task Workspace_OverPersistingStore_RenameRewritesAndPersistsLinks()
    {
        var store = await HydratedAsync();
        await KnowledgeSeeder.SeedIfEmptyAsync(store);
        using var workspace = new LocalKnowledgeWorkspace(store, new KnowledgeGraphService(store), NullLogger<LocalKnowledgeWorkspace>.Instance);

        var result = await workspace.RenameAsync("Getting Started/Install.md", "Setup");

        Assert.Equal("Getting Started/Setup.md", result.NewPath);
        Assert.Empty(result.FailedPages);
        Assert.Contains("[[Getting Started/Setup]]", _kv.Items[PageKey("Welcome.md")]);
        Assert.True(_kv.Items.ContainsKey(PageKey("Getting Started/Setup.md")));
        Assert.False(_kv.Items.ContainsKey(PageKey("Getting Started/Install.md")));
    }
}
