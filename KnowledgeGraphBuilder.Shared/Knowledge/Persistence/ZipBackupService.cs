using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;

namespace KnowledgeGraphBuilder.Shared.Knowledge.Persistence;

public enum ImportMode { Merge, Replace }

public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Rejected);

public interface IBackupService
{
    /// <summary>Downloads knowledge-graph-&lt;yyyyMMdd-HHmm&gt;.zip: pages and folders only, never settings.</summary>
    Task ExportAsync(CancellationToken ct = default);
    Task<ImportResult> ImportAsync(Stream zip, ImportMode mode, CancellationToken ct = default);
}

/// <summary>
/// Zip export and import. Extraction is manual because ZipFile.ExtractToDirectory aborts the WASM runtime.
/// </summary>
public sealed class ZipBackupService(IKnowledgeWorkspace workspace, IJSRuntime js) : IBackupService
{
    public const string ManifestName = "manifest.json";
    public const int Format = 1;
    static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task ExportAsync(CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await WriteExportAsync(buffer, ct);
        buffer.Position = 0;
        using var streamRef = new DotNetStreamReference(buffer, leaveOpen: true);
        await js.InvokeVoidAsync("kgb.saveAsFile", ct, $"knowledge-graph-{DateTime.Now:yyyyMMdd-HHmm}.zip", streamRef);
    }

    public async Task WriteExportAsync(Stream output, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = new JsonObject
        {
            ["app"] = "KnowledgeGraphBuilder",
            ["format"] = Format,
            ["exported"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await WriteEntryAsync(zip, ManifestName, manifest.ToJsonString(), ct);

        foreach (var folder in await AllFoldersAsync("", ct))
            zip.CreateEntry(folder + "/");

        foreach (var path in await workspace.GetAllPagePathsAsync(ct))
        {
            var page = await workspace.ReadPageAsync(path, ct);
            await WriteEntryAsync(zip, path, page.Markdown, ct);
        }
    }

    async Task<List<string>> AllFoldersAsync(string folder, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var node in await workspace.GetChildrenAsync(folder, ct))
        {
            if (!node.IsFolder) continue;
            result.Add(node.Path);
            result.AddRange(await AllFoldersAsync(node.Path, ct));
        }
        return result;
    }

    static async Task WriteEntryAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Utf8NoBom.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
    }

    public async Task<ImportResult> ImportAsync(Stream zip, ImportMode mode, CancellationToken ct = default)
    {
        // Read fully first: browser streams aren't seekable.
        using var buffer = new MemoryStream();
        await zip.CopyToAsync(buffer, ct);
        if (buffer.Length > KnowledgeStorageConstants.MaxImportBytes)
            throw new KnowledgeException("The backup is larger than 10 MB.");
        buffer.Position = 0;

        ZipArchive archive;
        try { archive = new ZipArchive(buffer, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new KnowledgeException("The file isn't a valid .zip backup."); }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(ManifestName);
            if (manifestEntry is not null)
            {
                using var reader = new StreamReader(manifestEntry.Open(), Utf8NoBom);
                try
                {
                    var manifest = JsonNode.Parse(await reader.ReadToEndAsync(ct));
                    var format = manifest?["format"]?.GetValue<int>() ?? Format;
                    if (format > Format) throw new KnowledgeException($"This backup uses format {format}, which this version can't read.");
                }
                catch (JsonException) { throw new KnowledgeException("The backup manifest is unreadable."); }
                catch (InvalidOperationException) { throw new KnowledgeException("The backup manifest is unreadable."); }
            }

            var rejected = new List<string>();
            var pages = new List<(KnowledgePath Path, ZipArchiveEntry Entry)>();
            var folders = new List<KnowledgePath>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name == ManifestName) continue;
                if (name.EndsWith('/'))
                {
                    if (KnowledgePath.TryParse(name.TrimEnd('/'), isPage: false, out var folder, out _) && !folder.IsRoot) folders.Add(folder);
                    else rejected.Add(name);
                    continue;
                }
                // Zip Slip guard: only validated, root-relative .md paths are accepted.
                if (!KnowledgePath.IsPagePath(name) ||
                    !KnowledgePath.TryParse(name, isPage: true, out var path, out _) ||
                    !string.Equals(path.Value, name, StringComparison.Ordinal))
                {
                    rejected.Add(name);
                    continue;
                }
                pages.Add((path, entry));
            }

            if (mode == ImportMode.Replace)
            {
                foreach (var node in await workspace.GetChildrenAsync("", ct))
                    await workspace.DeleteAsync(node.Path, ct);
            }

            foreach (var folder in folders.OrderBy(f => f.Value.Length))
            {
                if (!await workspace.ExistsAsync(folder.Value, ct))
                {
                    try { await workspace.CreateFolderAsync(folder.Value, ct); }
                    catch (KnowledgeAlreadyExistsException) { }
                }
            }

            int imported = 0, skipped = 0;
            foreach (var (path, entry) in pages)
            {
                string text;
                using (var reader = new StreamReader(entry.Open(), Utf8NoBom))
                    text = await reader.ReadToEndAsync(ct);
                var result = await workspace.SavePageAsync(path.Value, text, 0, ct);
                if (result.Status == SaveStatus.Created) imported++;
                else skipped++;
            }
            return new ImportResult(imported, skipped, rejected);
        }
    }
}
