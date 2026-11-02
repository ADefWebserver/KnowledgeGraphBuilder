using System.Text.Json;
using Microsoft.Extensions.Logging;
using static KnowledgeGraphBuilder.Shared.Knowledge.Persistence.KnowledgeStorageConstants;

namespace KnowledgeGraphBuilder.Shared.Knowledge.Persistence;

/// <summary>
/// WASM decorator: the in-memory file system is the working tree, and every change is written through to
/// localStorage (one key per page plus an index key). The index holds the authoritative modification ticks,
/// so concurrency checks don't depend on the in-memory file system keeping timestamps (R13).
/// </summary>
public sealed class PersistingKnowledgeStore : IKnowledgeStore
{
    readonly FileSystemKnowledgeStore _inner;
    readonly IKeyValueStore _kv;
    readonly ILogger<PersistingKnowledgeStore> _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    StorageIndex _index = StorageIndex.Empty();

    public PersistingKnowledgeStore(FileSystemKnowledgeStore inner, IKeyValueStore kv, ILogger<PersistingKnowledgeStore> log)
    {
        _inner = inner;
        _kv = kv;
        _log = log;
    }

    public event Action<KnowledgeChange>? ExternalChange;

    internal StorageIndex Index => _index;

    // ---------------------------------------------------------------- hydration

    public async Task HydrateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await HydrateCoreAsync(); }
        finally { _gate.Release(); }
    }

    async Task HydrateCoreAsync()
    {
        _inner.ResetRoot();
        var index = await ReadIndexAsync() ?? StorageIndex.Empty();
        var repaired = false;

        foreach (var (path, ticks) in index.Pages.ToList())
        {
            if (!KnowledgePath.TryParse(path, isPage: true, out var p, out _))
            {
                index.Pages.Remove(path);
                repaired = true;
                continue;
            }
            var markdown = await _kv.GetAsync(PageKey(path));
            if (markdown is null)
            {
                index.Pages.Remove(path);
                repaired = true;
                continue;
            }
            _inner.WriteRaw(p, markdown, ticks);
        }

        // Adopt page keys that the index doesn't list (for example after an interrupted write).
        foreach (var key in await _kv.KeysAsync(PageKeyPrefix))
        {
            var path = key[PageKeyPrefix.Length..];
            if (index.Pages.ContainsKey(path)) continue;
            if (!KnowledgePath.TryParse(path, isPage: true, out var p, out _) || p.Value != path) continue;
            var markdown = await _kv.GetAsync(key);
            if (markdown is null) continue;
            var ticks = DateTime.UtcNow.Ticks;
            _inner.WriteRaw(p, markdown, ticks);
            index.Pages[path] = ticks;
            AddFolders(index, p.Parent);
            repaired = true;
        }

        foreach (var folder in index.Folders.ToList())
        {
            if (KnowledgePath.TryParse(folder, isPage: false, out var f, out _) && !f.IsRoot)
                Directory.CreateDirectory(_inner.ToFullPath(f.Value));
            else
            {
                index.Folders.Remove(folder);
                repaired = true;
            }
        }
        foreach (var page in index.Pages.Keys) AddFolders(index, KnowledgePath.Parse(page, true).Parent);

        _index = index;
        if (repaired)
        {
            try { await WriteIndexAsync(_index); }
            catch (KnowledgeException ex) { _log.LogWarning("Couldn't write the repaired index: {Message}", ex.Message); }
        }
        _log.LogInformation("Hydrated {Pages} pages and {Folders} folders from browser storage", _index.Pages.Count, _index.Folders.Count);
    }

    /// <summary>Called by the storage-event bridge when another tab changes a key. A null key means storage was cleared.</summary>
    public async Task OnExternalKeyChangedAsync(string? key)
    {
        KnowledgeChange? change = null;
        await _gate.WaitAsync();
        try
        {
            if (key is null)
            {
                await HydrateCoreAsync();
                change = new KnowledgeChange(ChangeKind.Reloaded, "");
            }
            else if (key == IndexKey)
            {
                await ReconcileIndexAsync();
                change = new KnowledgeChange(ChangeKind.Reloaded, "");
            }
            else if (key.StartsWith(PageKeyPrefix, StringComparison.Ordinal))
            {
                change = await ReloadPageAsync(key[PageKeyPrefix.Length..]);
            }
        }
        finally { _gate.Release(); }

        if (change is not null) ExternalChange?.Invoke(change);
    }

    async Task<KnowledgeChange?> ReloadPageAsync(string path)
    {
        if (!KnowledgePath.TryParse(path, isPage: true, out var p, out _)) return null;
        var markdown = await _kv.GetAsync(PageKey(path));
        var existed = _index.Pages.TryGetValue(path, out var currentTicks);
        if (markdown is null)
        {
            if (!existed) return null;
            _inner.DeleteRaw(p);
            _index.Pages.Remove(path);
            return new KnowledgeChange(ChangeKind.Deleted, path);
        }

        var fresh = await ReadIndexAsync();
        var ticks = fresh is not null && fresh.Pages.TryGetValue(path, out var t) && t != currentTicks
            ? t
            : Math.Max(DateTime.UtcNow.Ticks, currentTicks + 1);
        _inner.WriteRaw(p, markdown, ticks);
        _index.Pages[path] = ticks;
        AddFolders(_index, p.Parent);
        return new KnowledgeChange(existed ? ChangeKind.Updated : ChangeKind.Created, path);
    }

    async Task ReconcileIndexAsync()
    {
        var fresh = await ReadIndexAsync() ?? StorageIndex.Empty();

        foreach (var path in _index.Pages.Keys.Except(fresh.Pages.Keys).ToList())
        {
            if (KnowledgePath.TryParse(path, true, out var p, out _)) _inner.DeleteRaw(p);
        }
        foreach (var folder in _index.Folders.Except(fresh.Folders).OrderByDescending(f => f.Length).ToList())
        {
            if (KnowledgePath.TryParse(folder, false, out var f, out _) && !f.IsRoot)
            {
                var full = _inner.ToFullPath(f.Value);
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
        }
        foreach (var folder in fresh.Folders)
        {
            if (KnowledgePath.TryParse(folder, false, out var f, out _) && !f.IsRoot)
                Directory.CreateDirectory(_inner.ToFullPath(f.Value));
        }
        foreach (var (path, ticks) in fresh.Pages)
        {
            if (!KnowledgePath.TryParse(path, true, out var p, out _)) continue;
            if (_index.Pages.TryGetValue(path, out var local) && local == ticks) continue;
            var markdown = await _kv.GetAsync(PageKey(path));
            if (markdown is null) continue;
            _inner.WriteRaw(p, markdown, ticks);
        }
        _index = fresh;
    }

    // ---------------------------------------------------------------- reads

    public async Task<IReadOnlyList<KnowledgeNode>> GetChildrenAsync(string folderPath, CancellationToken ct = default)
    {
        var nodes = await _inner.GetChildrenAsync(folderPath, ct);
        return nodes.Select(n => !n.IsFolder && _index.Pages.TryGetValue(n.Path, out var t) ? n with { ModifiedTicks = t } : n).ToList();
    }

    public async Task<PageContent> ReadPageAsync(string pagePath, CancellationToken ct = default)
    {
        var page = await _inner.ReadPageAsync(pagePath, ct);
        return _index.Pages.TryGetValue(page.Path, out var t) ? page with { ModifiedTicks = t } : page;
    }

    public Task<FolderStats> GetFolderStatsAsync(string folderPath, CancellationToken ct = default) => _inner.GetFolderStatsAsync(folderPath, ct);

    public Task<IReadOnlyList<string>> GetAllPagePathsAsync(CancellationToken ct = default) => _inner.GetAllPagePathsAsync(ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => _inner.ExistsAsync(path, ct);

    public async Task<StorageUsage> GetUsageAsync()
    {
        long bytes = 0;
        foreach (var key in await _kv.KeysAsync(KeyPrefix))
        {
            var value = await _kv.GetAsync(key) ?? "";
            bytes += (long)(key.Length + value.Length) * 2;
        }
        return new StorageUsage(bytes, AssumedQuotaBytes, _index.Pages.Count);
    }

    // ---------------------------------------------------------------- writes

    public async Task<SaveResult> SavePageAsync(string pagePath, string markdown, long? expectedModifiedTicks, CancellationToken ct = default)
    {
        var path = KnowledgePath.Parse(pagePath, isPage: true);
        await _gate.WaitAsync(ct);
        try
        {
            var existed = _index.Pages.TryGetValue(path.Value, out var currentTicks);
            if (expectedModifiedTicks == 0 && existed)
                return new SaveResult(SaveStatus.AlreadyExists, currentTicks, $"{path.Value} already exists.");
            if (expectedModifiedTicks > 0 && (!existed || currentTicks != expectedModifiedTicks))
                return new SaveResult(SaveStatus.Conflict, currentTicks, $"{path.Value} changed in another tab.");

            var previous = existed ? (await _inner.ReadPageAsync(path.Value, ct)).Markdown : null;
            var previousIndex = _index.Copy();

            var inner = await _inner.SavePageAsync(path.Value, markdown, null, ct);
            var newTicks = Math.Max(inner.ModifiedTicks, currentTicks + 1);
            _inner.SetTicks(path, newTicks);

            var next = _index.Copy();
            next.Pages[path.Value] = newTicks;
            AddFolders(next, path.Parent);

            try
            {
                await _kv.SetAsync(PageKey(path.Value), markdown);
                await WriteIndexAsync(next);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Roll back the in-memory change and the page key.
                if (previous is not null) _inner.WriteRaw(path, previous, currentTicks);
                else _inner.DeleteRaw(path);
                await TryRestoreKeyAsync(PageKey(path.Value), previous);
                RemoveNewFolders(previousIndex, next);
                _index = previousIndex;
                throw Wrap(ex, path.Value);
            }

            _index = next;
            return new SaveResult(existed ? SaveStatus.Saved : SaveStatus.Created, newTicks);
        }
        finally { _gate.Release(); }
    }

    public async Task CreateFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var path = KnowledgePath.Parse(folderPath, isPage: false);
        await _gate.WaitAsync(ct);
        try
        {
            await _inner.CreateFolderAsync(path.Value, ct);
            var next = _index.Copy();
            AddFolders(next, path);
            try { await WriteIndexAsync(next); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RemoveNewFolders(_index, next);
                throw Wrap(ex, path.Value);
            }
            _index = next;
        }
        finally { _gate.Release(); }
    }

    public Task<string> RenameAsync(string path, string newName, CancellationToken ct = default) =>
        RelocateAsync(path, () => _inner.RenameAsync(path, newName, ct), ct);

    public Task<string> MoveAsync(string path, string newParentFolder, CancellationToken ct = default) =>
        RelocateAsync(path, () => _inner.MoveAsync(path, newParentFolder, ct), ct);

    async Task<string> RelocateAsync(string rawPath, Func<Task<string>> relocate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var oldPath = await ResolveExistingPathAsync(rawPath, ct);
            var newPath = await relocate();
            if (newPath == oldPath) return newPath;

            var isPage = _index.Pages.ContainsKey(oldPath);
            var moved = MapPaths(_index.Pages.Keys, oldPath, newPath, isPage);

            var next = _index.Copy();
            foreach (var (from, _) in moved) next.Pages.Remove(from);
            foreach (var (from, to) in moved) next.Pages[to] = _index.Pages[from];
            if (!isPage)
            {
                var mapped = next.Folders.Select(f => MapPath(f, oldPath, newPath) ?? f).ToList();
                next.Folders.Clear();
                next.Folders.AddRange(mapped);
            }
            AddFolders(next, KnowledgePath.Parse(newPath, false).Parent);
            if (!isPage) AddFolders(next, KnowledgePath.Parse(newPath, false));

            var written = new List<string>();
            try
            {
                // Copy before delete: new keys, then the index, then remove the old keys.
                foreach (var (from, to) in moved)
                {
                    var markdown = await _kv.GetAsync(PageKey(from)) ?? (await _inner.ReadPageAsync(to, ct)).Markdown;
                    await _kv.SetAsync(PageKey(to), markdown);
                    written.Add(PageKey(to));
                }
                await WriteIndexAsync(next);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var key in written)
                {
                    try { await _kv.RemoveAsync(key); } catch (Exception) { /* best effort */ }
                }
                await UndoRelocateAsync(oldPath, newPath, isPage, ct);
                throw Wrap(ex, oldPath);
            }

            _index = next;
            foreach (var (from, to) in moved)
            {
                if (!string.Equals(from, to, StringComparison.Ordinal))
                {
                    try { await _kv.RemoveAsync(PageKey(from)); }
                    catch (Exception ex) { _log.LogWarning("Couldn't remove old key for {Path}: {Message}", from, ex.Message); }
                }
            }
            return newPath;
        }
        finally { _gate.Release(); }
    }

    async Task UndoRelocateAsync(string oldPath, string newPath, bool isPage, CancellationToken ct)
    {
        var oldP = KnowledgePath.Parse(oldPath, isPage);
        var newP = KnowledgePath.Parse(newPath, isPage);
        if (string.Equals(oldP.Parent.Value, newP.Parent.Value, StringComparison.OrdinalIgnoreCase))
            await _inner.RenameAsync(newPath, oldP.Name, ct);
        else
            await _inner.MoveAsync(newPath, oldP.Parent.Value, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var target = await ResolveExistingPathAsync(path, ct);
            var isPage = _index.Pages.ContainsKey(target);
            var removedPages = isPage
                ? [target]
                : _index.Pages.Keys.Where(p => p.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)).ToList();

            var next = _index.Copy();
            foreach (var p in removedPages) next.Pages.Remove(p);
            if (!isPage)
                next.Folders.RemoveAll(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase) || f.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase));

            try { await WriteIndexAsync(next); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw Wrap(ex, target); }

            try { await _inner.DeleteAsync(target, ct); }
            catch
            {
                await WriteIndexAsync(_index);
                throw;
            }

            _index = next;
            foreach (var p in removedPages)
            {
                try { await _kv.RemoveAsync(PageKey(p)); }
                catch (Exception ex) { _log.LogWarning("Couldn't remove key for {Path}: {Message}", p, ex.Message); }
            }
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- helpers

    async Task<string> ResolveExistingPathAsync(string raw, CancellationToken ct)
    {
        var p = KnowledgePath.Parse(raw, false);
        if (_index.Pages.ContainsKey(p.Value)) return p.Value;
        if (!KnowledgePath.IsPagePath(raw) && KnowledgePath.TryParse(raw, true, out var page, out _) && _index.Pages.ContainsKey(page.Value))
            return page.Value;
        if (await _inner.ExistsAsync(p.Value, ct)) return p.Value;
        throw new KnowledgeNotFoundException(p.Value);
    }

    static List<(string From, string To)> MapPaths(IEnumerable<string> pages, string oldPath, string newPath, bool isPage)
    {
        if (isPage) return [(oldPath, newPath)];
        return pages.Select(p => (p, MapPath(p, oldPath, newPath))).Where(t => t.Item2 is not null).Select(t => (t.p, t.Item2!)).ToList();
    }

    static string? MapPath(string path, string oldPrefix, string newPrefix)
    {
        if (string.Equals(path, oldPrefix, StringComparison.OrdinalIgnoreCase)) return newPrefix;
        if (path.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase)) return newPrefix + path[oldPrefix.Length..];
        return null;
    }

    static void AddFolders(StorageIndex index, KnowledgePath folder)
    {
        for (var f = folder; !f.IsRoot; f = f.Parent)
        {
            if (!index.Folders.Contains(f.Value, StringComparer.OrdinalIgnoreCase)) index.Folders.Add(f.Value);
        }
    }

    void RemoveNewFolders(StorageIndex before, StorageIndex after)
    {
        foreach (var folder in after.Folders.Except(before.Folders).OrderByDescending(f => f.Length))
        {
            var full = _inner.ToFullPath(folder);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
    }

    async Task TryRestoreKeyAsync(string key, string? previous)
    {
        try
        {
            if (previous is null) await _kv.RemoveAsync(key);
            else if (await _kv.GetAsync(key) != previous) await _kv.SetAsync(key, previous);
        }
        catch (Exception ex) { _log.LogWarning("Couldn't restore {Key}: {Message}", key, ex.Message); }
    }

    async Task<StorageIndex?> ReadIndexAsync()
    {
        var json = await _kv.GetAsync(IndexKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var index = JsonSerializer.Deserialize<StorageIndex>(json, KnowledgeStorageConstants.Json);
            if (index is null) return null;
            return new StorageIndex(index.Version, index.Folders ?? [], new Dictionary<string, long>(index.Pages ?? [], StringComparer.Ordinal));
        }
        catch (JsonException ex)
        {
            _log.LogWarning("The storage index is unreadable and will be rebuilt: {Message}", ex.Message);
            return null;
        }
    }

    Task WriteIndexAsync(StorageIndex index) =>
        _kv.SetAsync(IndexKey, JsonSerializer.Serialize(index, KnowledgeStorageConstants.Json));

    static Exception Wrap(Exception ex, string path) => ex switch
    {
        KnowledgeException => ex,
        _ => new KnowledgeIoException($"Couldn't write {path} to browser storage: {ex.Message}", ex),
    };
}
